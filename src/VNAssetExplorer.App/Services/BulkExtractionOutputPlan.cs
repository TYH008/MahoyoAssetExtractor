using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using VNAssetExplorer.Application;
using VNAssetExplorer.Core;

namespace VNAssetExplorer.App.Services;

public sealed record BulkOutputEntry(string ArchivePath, int EntryIndex, string Name, BulkExtractionCategory Category);
public sealed record BulkOutputItem(BulkOutputEntry Entry, string RelativePath, string FinalPath);

public sealed class BulkExtractionOutputPlan
{
    internal BulkOutputHooks? Hooks { get; set; }
    private BulkExtractionOutputPlan(BulkExtractionRequest request, BulkOutputItem[] items)
    {
        Request = request;
        Items = Array.AsReadOnly(items);
    }

    public BulkExtractionRequest Request { get; }
    public IReadOnlyList<BulkOutputItem> Items { get; }

    public static BulkExtractionOutputPlan Create(BulkExtractionRequest request, IEnumerable<BulkOutputEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(entries);
        ValidateRoots(request);
        using var guard = new BulkOutputDirectoryGuard();
        guard.Acquire(request.SourceDirectory, create: false);
        // Validate existing ancestors without creating any category or output directory.
        guard.Acquire(request.OutputDirectory, create: false, allowMissing: true);
        guard.EnsureSeparate(request.SourceDirectory, request.OutputDirectory);
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var identities = new HashSet<(string, int, BulkExtractionCategory)>();
        var items = new List<BulkOutputItem>();
        foreach (var entry in entries)
        {
            ArgumentNullException.ThrowIfNull(entry);
            ArgumentNullException.ThrowIfNull(entry.Name);
            if (entry.EntryIndex < 0 || !request.Categories.Contains(entry.Category))
                throw new ArgumentException("Entry index/category is not in the request.", nameof(entries));
            var archive = BulkOutputDirectoryGuard.ValidatePath(entry.ArchivePath);
            if (!IsWithin(archive, request.SourceDirectory))
                throw new ArgumentException("Archive must be under the source directory.", nameof(entries));
            var identity = ExtractionPath.Normalize(Path.GetRelativePath(request.SourceDirectory, archive)).ToUpperInvariant();
            if (!identities.Add((identity, entry.EntryIndex, entry.Category)))
                throw new ArgumentException("Duplicate archive entry identity.", nameof(entries));
            var archiveDirectory = "archive-" + Hash(identity);
            var extension = entry.Category switch
            {
                BulkExtractionCategory.Images => ".png",
                BulkExtractionCategory.GalleryImages => ".png",
                BulkExtractionCategory.Audio => ".ogg",
                BulkExtractionCategory.GalleryAudio => ".ogg",
                BulkExtractionCategory.Video => ".mp4",
                _ => throw new ArgumentOutOfRangeException(nameof(entries)),
            };
            var category = entry.Category switch
            {
                BulkExtractionCategory.Images => "图片",
                BulkExtractionCategory.GalleryImages => "画廊图片",
                BulkExtractionCategory.Audio => "音频",
                BulkExtractionCategory.GalleryAudio => "音频画廊",
                BulkExtractionCategory.Video => "视频",
                _ => throw new ArgumentOutOfRangeException(nameof(entries)),
            };
            var fileName = entry.EntryIndex.ToString("D10", CultureInfo.InvariantCulture) + "-" + SafeName(entry.Name) + extension;
            var relative = ExtractionPath.Normalize($"{category}/{archiveDirectory}/{fileName}");
            var final = ExtractionPath.ResolveUnderRoot(request.OutputDirectory, relative);
            if (final.Length >= 32760) throw new PathTooLongException("Output path exceeds the Windows long-path limit.");
            if (!paths.Add(final)) throw new ArgumentException("Output mapping collision.", nameof(entries));
            items.Add(new BulkOutputItem(entry with { ArchivePath = archive }, relative, final));
        }
        return new BulkExtractionOutputPlan(request, items.ToArray());
    }

    public Task<BulkWriteOutcome> WritePngAsync(BulkOutputItem item, ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken = default) =>
        WriteAsync(item, BulkExtractionCategory.Images,
            transaction => new AtomicPreviewPngWriter().WriteAsync(transaction, bytes, cancellationToken), cancellationToken);

    internal Task<BulkWriteOutcome> WritePngAsync(BulkOutputItem item, Func<CancellationToken, ReadOnlyMemory<byte>> payload, CancellationToken cancellationToken) =>
        WriteAsync(item, BulkExtractionCategory.Images,
            transaction => new AtomicPreviewPngWriter().WriteAsync(transaction, payload, cancellationToken), cancellationToken);

    public Task<BulkWriteOutcome> WriteOggAsync(BulkOutputItem item, Func<Stream, CancellationToken, Task> copyPayload, CancellationToken cancellationToken = default) =>
        WriteOggAsync(item, BulkExtractionCategory.Audio, copyPayload, cancellationToken);

    internal Task<BulkWriteOutcome> WriteOggAsync(BulkOutputItem item, BulkExtractionCategory category, Func<Stream, CancellationToken, Task> copyPayload, CancellationToken cancellationToken = default) =>
        WriteAsync(item, category,
            transaction => new AtomicCompatibleAudioWriter().WriteOggAsync(transaction, copyPayload, cancellationToken), cancellationToken);

    public Task<BulkWriteOutcome> WriteMp4Async(BulkOutputItem item, Func<Stream, CancellationToken, Task> copyPayload, CancellationToken cancellationToken = default) =>
        WriteAsync(item, BulkExtractionCategory.Video,
            transaction => new AtomicCompatibleVideoWriter().WriteMp4Async(transaction, copyPayload, cancellationToken), cancellationToken);

    public Task<BulkWriteOutcome> WriteBulkPngAsync(BulkOutputItem item, Action<Stream, CancellationToken> encode, CancellationToken token = default) =>
        WriteBulkPngAsync(item, BulkExtractionCategory.Images, encode, token);

    internal Task<BulkWriteOutcome> WriteBulkPngAsync(BulkOutputItem item, BulkExtractionCategory category,
        Action<Stream, CancellationToken> encode, CancellationToken token = default) =>
        WriteAsync(item, category,
            transaction => transaction.WriteAsync(async (stream, cancellation, beforeFlush) =>
            {
                encode(stream, cancellation);
                beforeFlush();
                await stream.FlushAsync(cancellation).ConfigureAwait(false);
                ((FileStream)stream).Flush(flushToDisk: true);
            }, 64 * 1024, token), token);

    private async Task<BulkWriteOutcome> WriteAsync(BulkOutputItem item, BulkExtractionCategory category, Func<BulkAtomicFileTransaction, Task<BulkWriteOutcome>> write, CancellationToken cancellationToken)
    {
        if (!Items.Contains(item) || item.Entry.Category != category)
            throw new ArgumentException("Item does not belong to this plan/category.", nameof(item));
        cancellationToken.ThrowIfCancellationRequested();
        ValidateRoots(Request);
        using var guard = new BulkOutputDirectoryGuard(Hooks);
        guard.Acquire(Request.SourceDirectory, create: false);
        // Validate both actual chains before any output creation.
        guard.Acquire(Request.OutputDirectory, create: false, allowMissing: true);
        guard.EnsureSeparate(Request.SourceDirectory, Request.OutputDirectory);
        var directory = guard.Acquire(Path.GetDirectoryName(item.FinalPath)!, create: true);
        return await write(new BulkAtomicFileTransaction(guard, directory, item.FinalPath, Hooks)).ConfigureAwait(false);
    }

    private static void ValidateRoots(BulkExtractionRequest request)
    {
        var source = BulkOutputDirectoryGuard.ValidatePath(request.SourceDirectory);
        var output = BulkOutputDirectoryGuard.ValidatePath(request.OutputDirectory);
        if (string.Equals(source, output, StringComparison.OrdinalIgnoreCase) || IsWithin(source, output) || IsWithin(output, source))
            throw new ArgumentException("Source and output directories must not contain each other.", nameof(request));
    }

    private static bool IsWithin(string path, string root) => path.StartsWith(
        Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    private static string Hash(string identity) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity))).ToLowerInvariant();

    private static string SafeName(string name)
    {
        var safe = new StringBuilder();
        foreach (var character in name)
        {
            if (safe.Length >= 64) break;
            safe.Append(char.IsLetterOrDigit(character) || character is '-' or '_' ? character : '_');
        }
        return safe.Length == 0 ? "entry" : safe.ToString();
    }
}
