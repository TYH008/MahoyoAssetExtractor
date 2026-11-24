using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;
using VNAssetExplorer.Core;

namespace VNAssetExplorer.App.Services;

internal sealed record BulkRootIdentity(string DosPath, string VolumePath, uint Volume, ulong FileId);

internal sealed class BulkOutputHooks
{
    internal Action<string, string>? Boundary { get; init; }
    internal Func<BulkRootIdentity, BulkRootIdentity>? ResolveRoot { get; init; }
    internal void At(string stage, string path) => Boundary?.Invoke(stage, path);
}

// Retain non-root locks through stream closure and handle-bound cleanup.
internal sealed class BulkOutputDirectoryGuard(BulkOutputHooks? hooks = null) : IDisposable
{
    private readonly List<(SafeFileHandle Handle, uint Volume, ulong Id)> handles = [];
    private readonly Dictionary<string, string> canonicalPaths = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, RootAnchor> anchors = new(StringComparer.OrdinalIgnoreCase);
    internal const int NameCollision = unchecked((int)0xc0000035);
    internal static bool Missing(int status) => status == unchecked((int)0xc0000034) || status == unchecked((int)0xc000003a);

    public SafeFileHandle Acquire(string path, bool create, bool allowMissing = false)
    {
        var fullPath = ValidatePath(path);
        var root = Path.GetPathRoot(fullPath)!;
        if (root.Length != 3 || root[1] != ':') throw new IOException("Only verified local volume roots are supported.");
        if (!anchors.TryGetValue(root, out var anchor))
        {
            // Open and validate the requested drive mapping once per guard lifetime.
            var handle = CreateFile(@"\\?\" + root, 0x80, 7, IntPtr.Zero, 3, 0x02200000, IntPtr.Zero);
            if (handle.IsInvalid)
            {
                handle.Dispose();
                throw new IOException("Cannot open volume anchor.", new Win32Exception(Marshal.GetLastWin32Error()));
            }
            Keep(handle);
            var info = Inspect(handle);
            var identity = new BulkRootIdentity(FinalName(handle, 0), FinalName(handle, 1), info.Volume, Id(info));
            identity = hooks?.ResolveRoot?.Invoke(identity) ?? identity;
            if (!string.Equals(identity.DosPath, @"\\?\" + root, StringComparison.OrdinalIgnoreCase)
                || !identity.VolumePath.StartsWith(@"\\?\Volume{", StringComparison.OrdinalIgnoreCase)
                || identity.VolumePath.IndexOf('}') != identity.VolumePath.Length - 2
                || !identity.VolumePath.EndsWith('\\') || identity.Volume != info.Volume || identity.FileId != Id(info))
                throw new IOException("Drive aliases or unverifiable volume roots are not allowed.");
            anchor = new RootAnchor(root, handle, identity);
            anchors.Add(root, anchor);
            hooks?.At("RootChecked", root);
        }
        var parent = anchor.Handle;
        canonicalPaths[fullPath] = anchor.Identity.VolumePath + fullPath[root.Length..];
        var current = Path.TrimEndingDirectorySeparator(anchor.OriginalRoot);
        foreach (var segment in fullPath[root.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            Revalidate();
            hooks?.At("BeforeDirectoryOpen", current);
            var status = OpenRelative(parent, segment, 0x80100000, 1, 1, 0x200021, out var child);
            if (Missing(status))
            {
                child.Dispose();
                if (allowMissing) return parent;
                if (!create) throw new DirectoryNotFoundException(current);
                hooks?.At("BeforeDirectoryCreate", current);
                Revalidate();
                status = OpenRelative(parent, segment, 0x80100000, 1, 3, 0x200021, out child);
            }
            if (status != 0) { child.Dispose(); throw Error("Open/create directory", status); }
            Keep(child);
            if (!string.Equals(FinalName(child, 0), @"\\?\" + current, StringComparison.OrdinalIgnoreCase))
                throw new IOException("Directory aliases are not allowed.");
            parent = child;
            hooks?.At("DirectoryLocked", current);
        }
        Revalidate();
        return parent;
    }

    public static string ValidatePath(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!Path.IsPathFullyQualified(path) || path.StartsWith(@"\\?\", StringComparison.Ordinal)
            || path.StartsWith(@"\\.\", StringComparison.Ordinal) || path.Contains('~'))
            throw new ArgumentException("Use an ordinary absolute path, not a Windows alias.", nameof(path));
        var root = Path.GetPathRoot(path)!;
        var relative = path[root.Length..].TrimEnd('\\', '/');
        if (relative.Length > 0)
        {
            if (relative.Split('\\', '/').Any(segment => segment.EndsWith(' ') || segment.EndsWith('.')))
                throw new ArgumentException("Windows trailing-space and trailing-dot aliases are not allowed.", nameof(path));
            ExtractionPath.Normalize(relative);
        }
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
    }

    private void Keep(SafeFileHandle handle)
    {
        try
        {
            var info = Inspect(handle);
            handles.Add((handle, info.Volume, Id(info)));
            ValidateType(info, true);
        }
        catch { if (!handles.Any(h => h.Handle == handle)) handle.Dispose(); throw; }
    }

    internal void Revalidate()
    {
        foreach (var item in handles)
        {
            var info = Inspect(item.Handle);
            ValidateType(info, true);
            if (info.Volume != item.Volume || Id(info) != item.Id) throw new IOException("Directory identity changed.");
        }
    }

    internal void EnsureSeparate(string source, string output)
    {
        var left = Path.TrimEndingDirectorySeparator(canonicalPaths[ValidatePath(source)]);
        var right = Path.TrimEndingDirectorySeparator(canonicalPaths[ValidatePath(output)]);
        if (string.Equals(left, right, StringComparison.OrdinalIgnoreCase)
            || left.StartsWith(right + "\\", StringComparison.OrdinalIgnoreCase)
            || right.StartsWith(left + "\\", StringComparison.OrdinalIgnoreCase))
            throw new IOException("Actual source and output locations overlap.");
        Revalidate();
        hooks?.At("DirectoriesSeparated", output);
    }

    internal bool ExistingFile(SafeFileHandle directory, string leaf)
    {
        var status = OpenRelative(directory, leaf, 0x80, 7, 1, 0x200000, out var file);
        using (file)
        {
            if (Missing(status)) return false;
            if (status != 0) throw Error("Inspect target", status);
            ValidateType(Inspect(file), false);
            return true;
        }
    }

    private static void ValidateType(FileInformation info, bool directory)
    {
        if ((info.Attributes & (uint)FileAttributes.ReparsePoint) != 0
            || ((info.Attributes & (uint)FileAttributes.Directory) != 0) != directory)
            throw new IOException("Unexpected directory/file type or reparse point.");
    }

    private static ulong Id(FileInformation info) => ((ulong)info.IndexHigh << 32) | info.IndexLow;
    private static FileInformation Inspect(SafeFileHandle handle)
    {
        if (!GetFileInformationByHandle(handle, out var info))
            throw new IOException("Cannot inspect handle identity.", new Win32Exception(Marshal.GetLastWin32Error()));
        return info;
    }

    private static string FinalName(SafeFileHandle handle, uint flags)
    {
        var name = new StringBuilder(32768);
        var length = GetFinalPathNameByHandle(handle, name, (uint)name.Capacity, flags);
        if (length == 0 || length >= name.Capacity) throw new IOException("Cannot resolve volume identity.", new Win32Exception(Marshal.GetLastWin32Error()));
        return name.ToString();
    }

    internal static int OpenRelative(SafeFileHandle parent, string leaf, uint access, uint share, uint disposition, uint options, out SafeFileHandle handle)
    {
        if (leaf.Length == 0 || leaf.Contains('\\') || leaf.Contains('/') || ExtractionPath.Normalize(leaf) != leaf)
            throw new ArgumentException("Native relative operations require one safe leaf.", nameof(leaf));
        var text = Marshal.StringToHGlobalUni(leaf);
        var namePointer = Marshal.AllocHGlobal(Marshal.SizeOf<UnicodeString>());
        var added = false;
        try
        {
            parent.DangerousAddRef(ref added);
            var length = checked((ushort)(leaf.Length * 2));
            Marshal.StructureToPtr(new UnicodeString { Length = length, MaximumLength = length, Buffer = text }, namePointer, false);
            var attributes = new ObjectAttributes { Length = (uint)Marshal.SizeOf<ObjectAttributes>(), Root = parent.DangerousGetHandle(), Name = namePointer, Attributes = 0x1040 };
            var status = NtCreateFile(out var raw, access, ref attributes, out _, IntPtr.Zero, 0x80, share, disposition, options, IntPtr.Zero, 0);
            handle = new SafeFileHandle(raw, true);
            return status;
        }
        finally
        {
            if (added) parent.DangerousRelease();
            Marshal.FreeHGlobal(namePointer);
            Marshal.FreeHGlobal(text);
        }
    }

    internal static IOException Error(string operation, int status) => new($"{operation} failed: NTSTATUS 0x{(uint)status:x8}.", new Win32Exception((int)RtlNtStatusToDosError(status)));
    public void Dispose()
    {
        for (var index = handles.Count - 1; index >= 0; index--) handles[index].Handle.Dispose();
        handles.Clear();
        anchors.Clear();
    }

    private sealed record RootAnchor(string OriginalRoot, SafeFileHandle Handle, BulkRootIdentity Identity);

    [StructLayout(LayoutKind.Sequential)]
    private struct FileInformation
    {
        public uint Attributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME Creation, Access, Write;
        public uint Volume, SizeHigh, SizeLow, Links, IndexHigh, IndexLow;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct UnicodeString { public ushort Length, MaximumLength; public IntPtr Buffer; }
    [StructLayout(LayoutKind.Sequential)]
    private struct ObjectAttributes { public uint Length; public IntPtr Root, Name; public uint Attributes; public IntPtr Security, Quality; }
    [StructLayout(LayoutKind.Sequential)]
    internal struct IoStatus { public IntPtr Status; public UIntPtr Information; }
    [DllImport("ntdll.dll")]
    private static extern int NtCreateFile(out IntPtr handle, uint access, ref ObjectAttributes attributes, out IoStatus io, IntPtr allocation, uint fileAttributes, uint share, uint disposition, uint options, IntPtr ea, uint eaLength);
    [DllImport("ntdll.dll")]
    private static extern uint RtlNtStatusToDosError(int status);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CreateFileW")]
    private static extern SafeFileHandle CreateFile(string path, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle handle, out FileInformation information);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "GetFinalPathNameByHandleW")]
    private static extern uint GetFinalPathNameByHandle(SafeFileHandle handle, StringBuilder name, uint size, uint flags);
}
