using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using VNAssetExplorer.App.Localization;
using VNAssetExplorer.App.Services;
using VNAssetExplorer.Application;

namespace VNAssetExplorer.App.ViewModels;

public enum AllImagesChoice { Back, Gallery, Continue }

public sealed class BulkExtractionViewModel : INotifyPropertyChanged
{
    private readonly IBulkMediaExtractionService service;
    private readonly IGameRootFolderPicker sourcePicker;
    private readonly IOutputFolderPicker outputPicker;
    private readonly Action<string> openOutput;
    private readonly Func<AllImagesChoice>? confirmAllImages;
    private readonly Action<string>? saveLanguage;
    private bool confirmingImages;
    private string statusKey = "Ready";
    private bool hasProgress;
    private BulkMediaProgress? progress;
    private string? resultKey;
    private Exception? resultError;
    private readonly List<Command> commands = [];
    private string sourceDirectory = "", outputDirectory = "";
    private bool images = true, audio = true, video = true, galleryImages, galleryAudio, closing, cancellationRequested;
    private long generation;
    private CancellationTokenSource? cancellation;
    private Task activeTask = Task.CompletedTask;

    public BulkExtractionViewModel(IBulkMediaExtractionService? service = null,
        IGameRootFolderPicker? sourcePicker = null, IOutputFolderPicker? outputPicker = null,
        Action<string>? openOutput = null, string? language = null, Action<string>? saveLanguage = null,
        Func<AllImagesChoice>? confirmAllImages = null)
    {
        Text = new AppText(language);
        this.saveLanguage = saveLanguage;
        this.confirmAllImages = confirmAllImages;
        this.service = service ?? new BulkMediaExtractionService();
        this.sourcePicker = sourcePicker ?? new GameRootFolderPicker(() => Text["PickSource"]);
        this.outputPicker = outputPicker ?? new OutputFolderPicker(() => Text["PickOutput"]);
        this.openOutput = openOutput ?? (path => Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }));
        PickSourceCommand = MakeCommand(() => Pick(true), () => CanEdit);
        PickOutputCommand = MakeCommand(() => Pick(false), () => CanEdit);
        StartCommand = MakeCommand(() => _ = StartAsync(), () => CanStart);
        CancelCommand = MakeCommand(RequestCancel, () => IsBusy && !cancellationRequested && !closing);
        OpenOutputCommand = MakeCommand(OpenOutput, () => CanEdit && Directory.Exists(CompletedOutputDirectory));
    }

    public AppText Text { get; private set; }
    public IReadOnlyList<LanguageOption> Languages => AppText.Languages;
    public LanguageOption SelectedLanguage
    {
        get => Languages.First(option => option.Code == Text.Code);
        set
        {
            if (value is null || value.Code == Text.Code || !Languages.Contains(value)) return;
            Text = new AppText(value.Code);
            saveLanguage?.Invoke(Text.Code);
            ChangedAll();
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public string SourceDirectory { get => sourceDirectory; set { if (CanEdit) { sourceDirectory = value; Changed(); } } }
    public string OutputDirectory { get => outputDirectory; set { if (CanEdit) { outputDirectory = value; Changed(); } } }
    public bool ImagesSelected { get => images; set { if (CanEdit) { images = value; Changed(); } } }
    public bool GalleryImagesSelected { get => galleryImages; set { if (CanEdit) { galleryImages = value; Changed(); } } }
    public bool AudioSelected { get => audio; set { if (CanEdit) { audio = value; Changed(); } } }
    public bool GalleryAudioSelected { get => galleryAudio; set { if (CanEdit) { galleryAudio = value; Changed(); } } }
    public bool VideoSelected { get => video; set { if (CanEdit) { video = value; Changed(); } } }
    public bool IsBusy { get; private set; }
    public bool CanEdit => !IsBusy && !closing && !confirmingImages;
    public bool CanStart => CanEdit && !string.IsNullOrWhiteSpace(SourceDirectory)
        && !string.IsNullOrWhiteSpace(OutputDirectory) && (images || audio || video || galleryImages || galleryAudio);
    public string StatusText => Text[statusKey];
    public string ProgressText => !hasProgress ? "" : progress?.ArchivePath is null ? Text["ScanTotalUnknown"] :
        Text.Format("WorkProgress", progress.ArchivePath, CategoryName(progress.Category), progress.ProcessedCount,
            (object?)progress.KnownTotalCount ?? Text["Unknown"]);
    public bool IsIndeterminate { get; private set; }
    public double ProgressMaximum { get; private set; } = 1;
    public double ProgressValue { get; private set; }
    public string ResultText
    {
        get
        {
            if (resultKey is null && resultError is null)
                return Report is null ? "" : FormatReport(Report);
            var description = resultKey is null ? "" : Text[resultKey];
            return resultError is null ? description :
                description.Length == 0 ? ErrorMessage(resultError) : $"{description}{Environment.NewLine}{ErrorMessage(resultError)}";
        }
    }
    public string CompletedOutputDirectory { get; private set; } = "";
    public BulkMediaExtractionReport? Report { get; private set; }
    public ICommand PickSourceCommand { get; }
    public ICommand PickOutputCommand { get; }
    public ICommand StartCommand { get; }
    public ICommand CancelCommand { get; }
    public ICommand OpenOutputCommand { get; }
    public Task ActiveTask => activeTask;

    public Task StartAsync()
    {
        if (!CanStart) return activeTask;
        var choice = AllImagesChoice.Continue;
        if (images && confirmAllImages is not null)
        {
            // A modal dialog runs a nested dispatcher. Guard against a second start while it is open.
            confirmingImages = true;
            try { choice = confirmAllImages(); }
            finally { confirmingImages = false; }
            if (choice == AllImagesChoice.Back || !CanStart) return activeTask;
        }
        var switchToGallery = choice == AllImagesChoice.Gallery;
        BulkExtractionRequest request;
        try
        {
            var categories = new List<BulkExtractionCategory>();
            if (images && !switchToGallery) categories.Add(BulkExtractionCategory.Images);
            if (galleryImages || switchToGallery) categories.Add(BulkExtractionCategory.GalleryImages);
            if (audio) categories.Add(BulkExtractionCategory.Audio);
            if (galleryAudio) categories.Add(BulkExtractionCategory.GalleryAudio);
            if (video) categories.Add(BulkExtractionCategory.Video);
            request = new(SourceDirectory, OutputDirectory, categories);
        }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or System.IO.PathTooLongException)
        {
            statusKey = "InvalidDirectory";
            resultKey = null;
            resultError = error;
            ChangedAll();
            return Task.CompletedTask;
        }
        if (switchToGallery)
        {
            images = false;
            galleryImages = true;
        }
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        activeTask = completion.Task;
        var run = ++generation;
        cancellation = new CancellationTokenSource();
        cancellationRequested = false;
        IsBusy = true;
        Report = null;
        resultKey = null;
        resultError = null;
        CompletedOutputDirectory = "";
        statusKey = "Scanning";
        hasProgress = true;
        progress = null;
        IsIndeterminate = true;
        ProgressValue = 0;
        ChangedAll();
        _ = RunAsync(request, run, cancellation, completion,
            SynchronizationContext.Current is System.Windows.Threading.DispatcherSynchronizationContext context ? context : null);
        return activeTask;
    }

    private async Task RunAsync(BulkExtractionRequest request, long run, CancellationTokenSource source,
        TaskCompletionSource completion, SynchronizationContext? context)
    {
        try
        {
            var report = await service.ExtractAsync(request, source.Token,
                new RunProgress(value =>
                {
                    void Publish()
                    {
                        if (run != generation || !IsBusy || closing || cancellationRequested) return;
                        statusKey = value.Phase switch { BulkMediaPhase.Scanning => "Scanning", BulkMediaPhase.Classifying => "Classifying", _ => "Writing" };
                        IsIndeterminate = value.KnownTotalCount is null;
                        ProgressMaximum = Math.Max(1, value.KnownTotalCount ?? 1);
                        ProgressValue = value.ProcessedCount;
                        progress = value;
                        ChangedAll();
                    }
                    if (context is null) Publish(); else context.Post(_ => Publish(), null);
                }));
            if (run != generation || closing) return;
            Report = report;
            CompletedOutputDirectory = request.OutputDirectory;
            var coverage = report.Result.ScanCoverage;
            var issues = report.FailureCount > 0 || report.Result.ScanOutcome != GameRootInventoryOutcome.Completed
                || coverage.UnreadableCount > 0 || coverage.UnreadableDirectoryCount > 0 || coverage.SkippedReparsePathCount > 0;
            statusKey = report.Result.Outcome switch
            {
                BulkExtractionOutcome.Cancelled => "Cancelled",
                BulkExtractionOutcome.Stopped => "Stopped",
                _ => issues ? "CompletedWithIssues" : "Completed",
            };
        }
        catch (OperationCanceledException) when (source.IsCancellationRequested)
        {
            if (run == generation && !closing) { statusKey = "Cancelled"; resultKey = "NoStatistics"; }
        }
        catch (Exception error)
        {
            if (run == generation && !closing) { statusKey = "ExtractionFailed"; resultKey = "IncompleteStatistics"; resultError = error; }
        }
        finally
        {
            source.Dispose();
            if (run == generation)
            {
                cancellation = null;
                IsBusy = false;
                IsIndeterminate = false;
                ChangedAll();
            }
            completion.TrySetResult();
        }
    }

    public void RequestCancel()
    {
        if (!IsBusy || cancellationRequested) return;
        cancellationRequested = true;
        statusKey = "Cancelling";
        cancellation?.Cancel();
        ChangedAll();
    }

    public async Task ShutdownAsync()
    {
        closing = true;
        RequestCancel();
        ChangedAll();
        await activeTask;
    }

    private void Pick(bool source)
    {
        if (!CanEdit) return;
        try
        {
            var path = source ? sourcePicker.PickGameRoot() : outputPicker.PickParentOutputDirectory(OutputDirectory);
            if (path is null) return;
            if (source) SourceDirectory = path; else OutputDirectory = path;
        }
        catch (Exception error) { statusKey = "PickFailed"; resultKey = null; resultError = error; ChangedAll(); }
    }

    private void OpenOutput()
    {
        if (!CanEdit || !Directory.Exists(CompletedOutputDirectory)) return;
        try { openOutput(CompletedOutputDirectory); }
        catch (Exception error) { statusKey = "OpenFailed"; resultKey = null; resultError = error; ChangedAll(); }
    }

    private string CategoryName(BulkExtractionCategory? category) => Text[category switch
    {
        BulkExtractionCategory.Images => "Images",
        BulkExtractionCategory.GalleryImages => "GalleryImages",
        BulkExtractionCategory.Audio => "Audio",
        BulkExtractionCategory.GalleryAudio => "GalleryAudio",
        BulkExtractionCategory.Video => "Video",
        _ => "Unspecified",
    }];

    private string ErrorMessage(Exception error) => error is GalleryExecutableException galleryError
        ? Text[galleryError.Reason switch
        {
            GalleryExecutableError.Missing => "GalleryExecutableMissing",
            GalleryExecutableError.UnsafePath => "GalleryExecutableUnsafe",
            _ => "GalleryExecutableUnverified",
        }]
        : error.Message;

    private string FormatReport(BulkMediaExtractionReport report)
    {
        var scan = report.Result.ScanCoverage;
        var scanOutcome = Text[report.Result.ScanOutcome switch
        {
            GameRootInventoryOutcome.Completed => "ScanCompleted",
            GameRootInventoryOutcome.Cancelled => "ScanCancelled",
            _ => "ScanFailed",
        }];
        var lines = new List<string>
        {
            Text.Format("ScanReport", scanOutcome, scan.ScannedFileCount, scan.ConfirmedArchiveCount,
                scan.UnrecognizedCount, scan.UnreadableCount, scan.UnreadableDirectoryCount, scan.SkippedReparsePathCount),
            Text.Format("ArchiveReport", report.OpenedArchiveCount, report.FailedArchiveCount,
                report.NotStartedArchiveCount, report.FailureCount),
        };
        foreach (var count in report.Result.Categories)
            lines.Add(Text.Format("CategoryReport", CategoryName(count.Category), count.MatchedCount, count.WrittenCount,
                count.ConflictSkippedCount, count.RecognitionFailedCount, count.WriteFailedCount, count.CancelledItemCount,
                count.NotStartedCount, count.UnmatchedCount));
        foreach (var failure in report.Failures)
            lines.Add(Text.Format("FailureReport", failure.ArchivePath ?? Text["ScanOrOutput"], CategoryName(failure.Category),
                (object?)failure.EntryIndex ?? Text["Unknown"], ErrorMessage(failure.Error)));
        return string.Join(Environment.NewLine, lines);
    }

    private Command MakeCommand(Action execute, Func<bool> canExecute)
    { var command = new Command(execute, canExecute); commands.Add(command); return command; }
    private void Changed([CallerMemberName] string? name = null)
    { PropertyChanged?.Invoke(this, new(name)); ChangedAll(); }
    private void ChangedAll()
    { PropertyChanged?.Invoke(this, new(null)); foreach (var command in commands) command.Refresh(); }
    private sealed class RunProgress(Action<BulkMediaProgress> report) : IProgress<BulkMediaProgress>
    { public void Report(BulkMediaProgress value) => report(value); }
    private sealed class Command(Action execute, Func<bool> canExecute) : ICommand
    {
        public event EventHandler? CanExecuteChanged;
        public bool CanExecute(object? parameter) => canExecute();
        public void Execute(object? parameter) { if (CanExecute(parameter)) execute(); }
        public void Refresh() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
    }
}
