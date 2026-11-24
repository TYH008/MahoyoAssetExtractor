using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using VNAssetExplorer.App.Services;
using VNAssetExplorer.Application;

namespace VNAssetExplorer.App.ViewModels;

public enum ArchiveViewState
{
    NotOpened,
    Loading,
    Opened,
    Empty,
    Cancelled,
    Error,
}

public sealed record MediaCategory(string Name, bool IsEnabled);

public sealed class MainWindowViewModel : INotifyPropertyChanged, IDisposable
{
    private readonly IFilePicker filePicker;
    private readonly IArchiveOpener archiveOpener;
    private readonly IGameRootFolderPicker gameRootFolderPicker;
    private readonly IGameRootInventoryService gameRootInventoryService;
    private CancellationTokenSource? gameRootScanCancellation;
    private TaskCompletionSource gameRootScanCompletion = CompletedSource();
    private string? gameRootPath;
    private IReadOnlyList<DiscoveredArchive> discoveredArchives = Array.Empty<DiscoveredArchive>();
    private DiscoveredArchive? selectedDiscoveredArchive;
    private string gameRootCoverageSummaryText = string.Empty;
    private string gameRootCoverageWarningText = string.Empty;
    private bool isGameRootScanning;
    private readonly IImageClassificationService imageClassificationService;
    private readonly IAudioClassificationService audioClassificationService;
    private readonly IVideoClassificationService videoClassificationService;
    private readonly VNAssetExplorer.App.Services.IImageClassificationPreviewService? imageClassificationPreviewService;
    private IReadOnlyList<ClassifiedImage> classifiedImages = Array.Empty<ClassifiedImage>();
    private string imageClassificationStatusText = string.Empty;
    private string imageClassificationSummaryText = string.Empty;
    private IReadOnlyList<ClassifiedAudio> classifiedAudio = Array.Empty<ClassifiedAudio>();
    private IReadOnlyList<ClassifiedAudio> selectedClassifiedAudio = Array.Empty<ClassifiedAudio>();
    private string audioClassificationStatusText = string.Empty;
    private CancellationTokenSource? audioClassificationCancellation;
    private TaskCompletionSource audioClassificationCompletion = CompletedSource();
    private AudioClassificationRequest? currentAudioClassificationRequest;
    private bool isClassifyingAudio;
    private bool isAudioMode;
    private ClassifiedAudio? selectedClassifiedAudioFocus;
    private IReadOnlyList<ClassifiedVideo> classifiedVideo = Array.Empty<ClassifiedVideo>();
    private IReadOnlyList<ClassifiedVideo> selectedClassifiedVideo = Array.Empty<ClassifiedVideo>();
    private string videoClassificationStatusText = string.Empty;
    private CancellationTokenSource? videoClassificationCancellation;
    private TaskCompletionSource videoClassificationCompletion = CompletedSource();
    private VideoClassificationRequest? currentVideoClassificationRequest;
    private bool isClassifyingVideo;
    private bool isVideoMode;
    private ClassifiedVideo? selectedClassifiedVideoFocus;
    private CancellationTokenSource? imageClassificationCancellation;
    private TaskCompletionSource imageClassificationCompletion = CompletedSource();
    private bool isClassifyingImages;
    private ClassifiedImage? selectedClassifiedImage;
    private IReadOnlyList<ClassifiedImage> selectedClassifiedImages = Array.Empty<ClassifiedImage>();
    private System.Windows.Media.Imaging.BitmapSource? classifiedPreviewImage;
    private byte[]? classifiedPreviewPngBytes;
    private readonly SemaphoreSlim classifiedPreviewGate = new(1, 1);
    private readonly HashSet<ClassifiedPreviewRequest> activeClassifiedPreviewRequests = [];
    private ImageSortKey imageSortKey = ImageSortKey.ResourceOrder;
    private ImageSortDirection imageSortDirection = ImageSortDirection.Ascending;
    private readonly IOutputFolderPicker? outputFolderPicker;
    private readonly IExtractionConfirmationService confirmationService;
    private readonly IExtractionCancellationConfirmationService cancellationConfirmationService;
    private readonly IApplicationExtractionDecisionProvider extractionDecisionProvider;
    private readonly IAvailableSpaceProvider? availableSpaceProvider;
    private readonly IAutoPreviewDelay autoPreviewDelay;
    private readonly IPreviewPngSaveDialog previewPngSaveDialog;
    private readonly IPreviewPngWriter previewPngWriter;
    private readonly ICompatibleAudioSaveDialog? compatibleAudioSaveDialog;
    private readonly ICompatibleAudioWriter compatibleAudioWriter;
    private readonly ICompatibleVideoSaveDialog compatibleVideoSaveDialog;
    private readonly ICompatibleVideoWriter compatibleVideoWriter;
    private readonly IRawResourceSaveDialog rawResourceSaveDialog;
    private readonly IRawResourceWriter rawResourceWriter;
    private readonly IBatchPngOutputFolderPicker batchPngOutputFolderPicker;
    private readonly IBatchPngConflictConfirmationService batchPngConflictConfirmationService;
    private readonly BatchPngExportExecutor batchPngExportExecutor = new();
    private string? recentOutputParentDirectory;
    private CancellationTokenSource? openCancellation;
    private string? pendingTrustedOpenPath;
    private long operationVersion;
    private ArchiveViewState state = ArchiveViewState.NotOpened;
    private string statusText = "请选择一个 HFA 文件。源归档始终只读。";
    private string? currentFileName;
    private IArchiveSession? previewSession;
    private ArchiveEntryProjection? selectedEntry;
    private System.Windows.Media.Imaging.BitmapSource? previewImage;
    private string previewStatusText = "请选择一个 MZP 条目。";
    private string transparencyHintText = string.Empty;
    private long previewVersion;
    private bool isPreviewLoading;
    private bool isUnsupportedPreviewSelection;
    private PreviewRequest? currentPreviewRequest;
    private bool disposed;
    private PreviewSaveSnapshot? previewSaveSnapshot;
    private bool isSavingPreviewPng;
    private string previewSaveStatusText = string.Empty;
    private ArchiveEntryProjection? autoPreviewSuppressedEntry;
    private TaskCompletionSource extractionCompletion = CompletedSource();
    private CancellationTokenSource? extractionCancellation;
    private bool extractionCancellationRequested;
    private readonly SemaphoreSlim previewSessionGate = new(1, 1);
    private readonly HashSet<PreviewRequest> activePreviewRequests = [];
    private readonly HashSet<SaveRequest> activeSaveRequests = [];
    private IReadOnlySet<int> selectedArchiveIndices = new HashSet<int>();
    private PreparedExtraction? preparedExtraction;
    private IArchiveSession? preparedSession;
    private BatchPngRequest? activeBatchPngRequest;
    private ClassifiedExportRequest? activeClassifiedExportRequest;
    private bool isBatchExportingPng;
    private bool isCancellingBatchExportPng;
    private string batchExportProgressText = string.Empty;
    private string batchExportSummaryText = string.Empty;
    private string mainExportStatusText = string.Empty;


    public MainWindowViewModel(IFilePicker filePicker, IArchiveOpener archiveOpener, IOutputFolderPicker? outputFolderPicker = null, IExtractionConfirmationService? confirmationService = null, IExtractionCancellationConfirmationService? cancellationConfirmationService = null, IApplicationExtractionDecisionProvider? extractionDecisionProvider = null, IAvailableSpaceProvider? availableSpaceProvider = null, IAutoPreviewDelay? autoPreviewDelay = null, IPreviewPngSaveDialog? previewPngSaveDialog = null, IPreviewPngWriter? previewPngWriter = null, IBatchPngOutputFolderPicker? batchPngOutputFolderPicker = null, IBatchPngConflictConfirmationService? batchPngConflictConfirmationService = null, IGameRootFolderPicker? gameRootFolderPicker = null, IGameRootInventoryService? gameRootInventoryService = null, IImageClassificationService? imageClassificationService = null, VNAssetExplorer.App.Services.IImageClassificationPreviewService? imageClassificationPreviewService = null, IAudioClassificationService? audioClassificationService = null, ICompatibleAudioSaveDialog? compatibleAudioSaveDialog = null, ICompatibleAudioWriter? compatibleAudioWriter = null, IRawResourceSaveDialog? rawResourceSaveDialog = null, IRawResourceWriter? rawResourceWriter = null, IVideoClassificationService? videoClassificationService = null, ICompatibleVideoSaveDialog? compatibleVideoSaveDialog = null, ICompatibleVideoWriter? compatibleVideoWriter = null)
    {
        this.filePicker = filePicker;
        this.archiveOpener = archiveOpener;
        this.gameRootFolderPicker = gameRootFolderPicker ?? new GameRootFolderPicker();
        this.gameRootInventoryService = gameRootInventoryService ?? new GameRootInventoryServiceAdapter(new GameRootInventoryService());
        this.imageClassificationService = imageClassificationService ?? new ImageClassificationServiceAdapter(new ImageClassificationService());
        this.audioClassificationService = audioClassificationService ?? new AudioClassificationServiceAdapter(new AudioClassificationService());
        this.videoClassificationService = videoClassificationService ?? new VideoClassificationServiceAdapter(new VideoClassificationService());
        this.imageClassificationPreviewService = imageClassificationPreviewService ?? imageClassificationService as VNAssetExplorer.App.Services.IImageClassificationPreviewService ?? this.imageClassificationService as VNAssetExplorer.App.Services.IImageClassificationPreviewService;
        this.outputFolderPicker = outputFolderPicker;
        this.confirmationService = confirmationService ?? new CancelledExtractionConfirmationService();
        this.cancellationConfirmationService = cancellationConfirmationService ?? new DeclineCancellationConfirmationService();
        this.extractionDecisionProvider = extractionDecisionProvider ?? new SkipExtractionDecisionProvider();
        this.availableSpaceProvider = availableSpaceProvider;
        this.autoPreviewDelay = autoPreviewDelay ?? new TaskAutoPreviewDelay();
        this.previewPngSaveDialog = previewPngSaveDialog ?? new PreviewPngSaveDialog();
        this.previewPngWriter = previewPngWriter ?? new AtomicPreviewPngWriter();
        this.compatibleAudioSaveDialog = compatibleAudioSaveDialog ?? new CompatibleAudioSaveDialog();
        this.compatibleAudioWriter = compatibleAudioWriter ?? new AtomicCompatibleAudioWriter();
        this.compatibleVideoSaveDialog = compatibleVideoSaveDialog ?? new CompatibleVideoSaveDialog();
        this.compatibleVideoWriter = compatibleVideoWriter ?? new AtomicCompatibleVideoWriter();
        this.rawResourceSaveDialog = rawResourceSaveDialog ?? new RawResourceSaveDialog();
        this.rawResourceWriter = rawResourceWriter ?? new AtomicRawResourceWriter();
        this.batchPngOutputFolderPicker = batchPngOutputFolderPicker ?? new OutputFolderPicker();
        this.batchPngConflictConfirmationService = batchPngConflictConfirmationService ?? new BatchPngConflictConfirmationService();
        OpenCommand = new AsyncCommand(OpenAsync, () => !IsLoading && !IsExtracting && !IsBatchExportingPng);
        SelectGameRootCommand = new AsyncCommand(SelectGameRootAsync, () => !IsLoading && !IsExtracting && !IsBatchExportingPng && !IsGameRootScanning);
        CancelGameRootScanCommand = new AsyncCommand(CancelGameRootScanAsync, () => IsGameRootScanning);
        CancelImageClassificationCommand = new AsyncCommand(CancelImageClassificationAsync, () => CanCancelImageClassification);
        OpenDiscoveredArchiveCommand = new AsyncCommand(OpenDiscoveredArchiveAsync, () => SelectedDiscoveredArchive is not null && !IsGameRootScanning && !IsLoading && !IsExtracting && !IsBatchExportingPng);
        SelectAllCommand = new SyncCommand(SelectAllEntries, () => !IsLoading && !IsExtracting && !IsBatchExportingPng && Entries.Count > 0);
        ExtractSelectedCommand = new AsyncCommand(() => ExtractAsync(false), () => CanExtractSelected && !IsExtracting && !IsBatchExportingPng);
        ExtractAllCommand = new AsyncCommand(() => ExtractAsync(true), () => CanExtractAll && !IsExtracting && !IsBatchExportingPng);
        CancelExtractionCommand = new SyncCommand(CancelExtraction, () => IsExtracting && !IsCancellingExtraction);
        PreviewCommand = new AsyncCommand(PreviewAsync, () => SelectedEntry is not null && (!IsPreviewLoading || currentPreviewRequest?.Phase == PreviewPhase.PendingDelay) && !IsExtracting && !IsBatchExportingPng && previewSession is not null);
        CancelPreviewCommand = new AsyncCommand(CancelPreviewAsync, () => currentPreviewRequest is not null && !IsBatchExportingPng);
        SavePreviewPngCommand = new AsyncCommand(SavePreviewPngAsync, () => CanSavePreviewPng && !IsBatchExportingPng);
        BatchExportSelectedPngCommand = new AsyncCommand(BatchExportSelectedPngAsync, () => CanBatchExportSelectedPng);
        CancelBatchExportPngCommand = new SyncCommand(CancelBatchExportPng, () => IsBatchExportingPng && !IsCancellingBatchExportPng);
        ExportCommand = new AsyncCommand(ExportAsync, () => CanExport);
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public event EventHandler? SelectAllRequested;

    public ICommand OpenCommand { get; }
    public ICommand SelectGameRootCommand { get; }
    public ICommand OpenDiscoveredArchiveCommand { get; }
    public ICommand CancelGameRootScanCommand { get; }
    public ICommand CancelImageClassificationCommand { get; }
    public ICommand SelectAllCommand { get; }
    public ICommand PreviewCommand { get; }
    public ICommand CancelPreviewCommand { get; }
    public ICommand SavePreviewPngCommand { get; }
    public ICommand ExtractSelectedCommand { get; }
    public ICommand ExtractAllCommand { get; }
    public ICommand CancelExtractionCommand { get; }
    public ICommand BatchExportSelectedPngCommand { get; }
    public ICommand CancelBatchExportPngCommand { get; }
    public ICommand ExportCommand { get; }
    public string ExtractionUnavailableText => "安全提取功能正在接入，当前不可用。";
    public bool IsClassifyingImages => isClassifyingImages;
    public bool CanCancelImageClassification => isClassifyingImages;
    public bool IsClassifyingAudio => isClassifyingAudio;
    public bool IsAudioMode => isAudioMode;
    public bool IsVideoMode => isVideoMode;
    public IReadOnlyList<ClassifiedAudio> ClassifiedAudio => classifiedAudio;
    public ClassifiedAudio? SelectedClassifiedAudioFocus => selectedClassifiedAudioFocus;
    public string AudioClassificationStatusText => audioClassificationStatusText;
    public bool IsClassifyingVideo => isClassifyingVideo;
    public IReadOnlyList<ClassifiedVideo> ClassifiedVideo => classifiedVideo;
    public ClassifiedVideo? SelectedClassifiedVideoFocus => selectedClassifiedVideoFocus;
    public string VideoClassificationStatusText => videoClassificationStatusText;
    public IReadOnlyList<ClassifiedImage> ClassifiedImages => classifiedImages;
    public ClassifiedImage? SelectedClassifiedImage => selectedClassifiedImage;
    public int SelectedClassifiedImageCount => selectedClassifiedImages.Count;
    public System.Windows.Media.Imaging.BitmapSource? ClassifiedPreviewImage => classifiedPreviewImage;
    public ImageSortKey ImageSortKey => imageSortKey;
    public ImageSortDirection ImageSortDirection => imageSortDirection;
    public string ImageClassificationStatusText => imageClassificationStatusText;
    public string ImageClassificationSummaryText => imageClassificationSummaryText;
    public IReadOnlyList<MediaCategory> MediaCategories { get; } =
    [
        new("图片", true),
        new("音频", true),
        new("视频", true),
    ];

    public string? RecentOutputParentDirectory => recentOutputParentDirectory;
    public IReadOnlyList<DiscoveredArchive> DiscoveredArchives => discoveredArchives;
    public DiscoveredArchive? SelectedDiscoveredArchive { get => selectedDiscoveredArchive; set { selectedDiscoveredArchive = value; OnPropertyChanged(nameof(SelectedDiscoveredArchive)); ((AsyncCommand)OpenDiscoveredArchiveCommand).RaiseCanExecuteChanged(); } }
    public string? GameRootPath => gameRootPath;
    public string GameRootCoverageSummaryText => gameRootCoverageSummaryText;
    public string GameRootCoverageWarningText => gameRootCoverageWarningText;
    public bool IsGameRootScanning => isGameRootScanning;
    public bool IsExtracting { get; private set; }
    public bool IsCancellingExtraction { get; private set; }
    public bool IsEntryListInteractive => !IsExtracting && !IsBatchExportingPng;
    public string ExtractionStatusText { get; private set; } = string.Empty;
    public string ExtractionProgressText { get; private set; } = string.Empty;
    public string ExtractionSummaryText { get; private set; } = string.Empty;
    public bool CanSavePreviewPng => HasValidPreviewSaveSnapshot && !IsLoading && !IsPreviewLoading && !IsExtracting && !IsSavingPreviewPng && !IsBatchExportingPng && !disposed;
    public bool IsSavingPreviewPng => isSavingPreviewPng;
    public string PreviewSaveStatusText => previewSaveStatusText;
    public bool IsBatchExportingPng => isBatchExportingPng;
    public bool IsCancellingBatchExportPng => isCancellingBatchExportPng;
    public string BatchExportProgressText => batchExportProgressText;
    public string BatchExportSummaryText => batchExportSummaryText;
    public string MainExportStatusText => mainExportStatusText;
    public bool CanBatchExportSelectedPng => !disposed && !IsLoading && !IsPreviewLoading && !IsExtracting && !IsSavingPreviewPng && !IsBatchExportingPng && previewSession is IArchivePreviewSession && SelectedCount > 0;
    public bool CanExport => !disposed && !IsLoading && !IsPreviewLoading && !IsExtracting && !IsSavingPreviewPng && !IsBatchExportingPng &&
        (IsAudioMode
            ? SelectedClassifiedAudioCount > 0
            : IsVideoMode
                ? selectedClassifiedVideo.Count > 0
                : (SelectedClassifiedImageCount > 0 && classifiedPreviewPngBytes is not null) || (previewSession is IArchivePreviewSession && SelectedCount > 0));
    public string ExportSelectionText => $"导出（{(IsAudioMode ? SelectedClassifiedAudioCount : IsVideoMode ? selectedClassifiedVideo.Count : SelectedClassifiedImageCount > 0 ? SelectedClassifiedImageCount : SelectedCount)}）";
    public ExportCapability? PendingExportCapability { get; private set; }

    public void SetExportCapabilityForTesting(ExportCapability? capability) => PendingExportCapability = capability;

    public void SetRecentOutputParentDirectory(string? directory)
    {
        recentOutputParentDirectory = directory;
        OnPropertyChanged(nameof(RecentOutputParentDirectory));
    }

    public bool TryBuildExtractionConfirmation(bool all, out ExtractionConfirmationRequest? request)
    {
        request = null;
        IArchivePreviewSession? session = previewSession as IArchivePreviewSession;
        var indices = all ? FreezeAllArchiveIndices() : FreezeSelectedArchiveIndices();
        if (session is null || indices.Count == 0 || outputFolderPicker is null)
        {
            return false;
        }

        var parent = outputFolderPicker.PickParentOutputDirectory(recentOutputParentDirectory);
        if (parent is null)
        {
            return false;
        }

        try
        {
            var plan = session.PlanExtraction(indices, parent, availableSpaceProvider: availableSpaceProvider is null ? null : GetAvailableBytesSafely);
            request = new ExtractionConfirmationRequest(all ? "全部资源" : "选中资源", indices.Count, plan.OutputRoot, plan.TotalBytes, plan.SpaceStatus, all);
            preparedExtraction = new PreparedExtraction(session, indices, plan, request);
            preparedSession = session;
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private long? GetAvailableBytesSafely(string outputRoot)
    {
        try
        {
            return availableSpaceProvider?.GetAvailableBytes(outputRoot);
        }
        catch (UnauthorizedAccessException) { return null; }
        catch (IOException) { return null; }
        catch (ArgumentException) { return null; }
    }

    private async Task ExtractAsync(bool all)
    {
        if (!TryBuildExtractionConfirmation(all, out var request) || request is null || preparedExtraction is null || !confirmationService.Confirm(request)) return;
        var prepared = preparedExtraction;
        preparedExtraction = null;
        preparedSession = null;
        IArchivePreviewSession? session = prepared.Session as IArchivePreviewSession;
        var indices = prepared.Indices;
        var plan = prepared.Plan;
        if (session is null) return;
        var version = ++operationVersion;
        extractionCancellation?.Dispose();
        extractionCancellation = new CancellationTokenSource();
        extractionCancellationRequested = false;
        IsCancellingExtraction = false;
        extractionCompletion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        IsExtracting = true;
        SetExtractionCommandsEnabled(false);
        ExtractionStatusText = all ? $"正在提取全部 {indices.Count} 项" : $"正在提取已选 {indices.Count} 项";
        ExtractionProgressText = "正在开始……";
        NotifyExtractionState();
        var progressClosed = 0;
        try
        {
            var progress = new Progress<ApplicationExtractionProgress>(value =>
            {
                if (Volatile.Read(ref progressClosed) != 0 || version != operationVersion || disposed) return;
                ExtractionProgressText = $"正在处理 {value.Position}/{value.Total}：{value.Name}（{value.Phase}）";
                OnPropertyChanged(nameof(ExtractionProgressText));
            });
            var gateEntered = false;
            try
            {
                await previewSessionGate.WaitAsync(extractionCancellation.Token);
                gateEntered = true;
                var summary = await session.ExtractAsync(plan, extractionDecisionProvider, extractionCancellation.Token, progress);
                Interlocked.Exchange(ref progressClosed, 1);
                if (version == operationVersion && !disposed)
                {
                    var written = summary.Results.Count(result => result.Status == "Written");
                    var renamed = summary.Results.Count(result => result.Status == "Renamed");
                    var skipped = summary.Results.Count(result => result.Status == "Skipped");
                    var failed = summary.Results.Count(result => result.Status == "Failed");
                    var cancelled = summary.Results.Count(result => result.Status == "Cancelled");
                    ExtractionSummaryText = $"完成：{written} 写入，{renamed} 重命名，{skipped} 跳过，{failed} 失败，{cancelled} 取消，{(summary.Cancelled ? "已取消" : "未取消")}。\n输出目录：{request.OutputRoot}";
                    if (!summary.Cancelled)
                    {
                        ExtractionProgressText = $"处理完成：{indices.Count}/{indices.Count} 项。";
                        OnPropertyChanged(nameof(ExtractionProgressText));
                    }
                    OnPropertyChanged(nameof(ExtractionSummaryText));
                    PublishMainExportStatus(ExtractionSummaryText);
                }
            }
            finally
            {
                if (gateEntered)
                {
                    previewSessionGate.Release();
                }
            }
        }
        catch (Exception) when (version != operationVersion || disposed) { }
        catch (Exception)
        {
            ExtractionSummaryText = "提取失败：内部错误。已完成文件保留，当前临时文件由 Application/Core 清理。";
            OnPropertyChanged(nameof(ExtractionSummaryText));
        }
        finally
        {
            Interlocked.Exchange(ref progressClosed, 1);
            IsExtracting = false;
            IsCancellingExtraction = false;
            if (version == operationVersion && !disposed)
            {
                ExtractionStatusText = string.Empty;
                extractionCancellation?.Dispose();
                extractionCancellation = null;
                extractionCompletion.TrySetResult();
                SetExtractionCommandsEnabled(true);
                NotifyExtractionState();
            }
            if (disposed)
            {
                extractionCancellation?.Dispose();
                extractionCancellation = null;
                previewSession?.Dispose();
                previewSession = null;
            }
            extractionCompletion.TrySetResult();
            if (version == operationVersion && !disposed)
            {
                NotifyExtractionState();
            }
        }
    }

    private void NotifyExtractionState()
    {
        OnPropertyChanged(nameof(IsExtracting));
        OnPropertyChanged(nameof(IsCancellingExtraction));
        OnPropertyChanged(nameof(IsEntryListInteractive));
        OnPropertyChanged(nameof(ExtractionStatusText));
        ((AsyncCommand)ExtractSelectedCommand).RaiseCanExecuteChanged();
        ((AsyncCommand)ExtractAllCommand).RaiseCanExecuteChanged();
        ((AsyncCommand)OpenCommand).RaiseCanExecuteChanged();
        ((AsyncCommand)PreviewCommand).RaiseCanExecuteChanged();
        ((SyncCommand)CancelExtractionCommand).RaiseCanExecuteChanged();
    }

    private void SetExtractionCommandsEnabled(bool enabled)
    {
        OnPropertyChanged(nameof(CanExtractSelected));
        OnPropertyChanged(nameof(CanExtractAll));
        ((AsyncCommand)OpenCommand).RaiseCanExecuteChanged();
        ((SyncCommand)SelectAllCommand).RaiseCanExecuteChanged();
        ((AsyncCommand)PreviewCommand).RaiseCanExecuteChanged();
        ((AsyncCommand)ExtractSelectedCommand).RaiseCanExecuteChanged();
        ((AsyncCommand)ExtractAllCommand).RaiseCanExecuteChanged();
    }

    private void CancelExtraction()
    {
        if (IsExtracting && !extractionCancellationRequested && extractionCancellation is not null && cancellationConfirmationService.ConfirmCancelExtraction())
        {
            extractionCancellationRequested = true;
            IsCancellingExtraction = true;
            ExtractionStatusText = "正在取消……已完成文件保留，当前临时文件由 Application/Core 清理。";
            OnPropertyChanged(nameof(ExtractionStatusText));
            OnPropertyChanged(nameof(IsCancellingExtraction));
            extractionCancellation.Cancel();
            ((SyncCommand)CancelExtractionCommand).RaiseCanExecuteChanged();
        }
    }

    public async Task ShutdownAsync()
    {
        if (disposed) return;
        var scanCompletion = gameRootScanCompletion;
        gameRootScanCancellation?.Cancel();
        await scanCompletion.Task.ConfigureAwait(true);
        var imageClassificationCompletion = this.imageClassificationCompletion;
        imageClassificationCancellation?.Cancel();
        await imageClassificationCompletion.Task.ConfigureAwait(true);
        var audioClassificationCompletion = this.audioClassificationCompletion;
        audioClassificationCancellation?.Cancel();
        await audioClassificationCompletion.Task.ConfigureAwait(true);
        var videoClassificationCompletion = this.videoClassificationCompletion;
        videoClassificationCancellation?.Cancel();
        await videoClassificationCompletion.Task.ConfigureAwait(true);
        var batchRequest = activeBatchPngRequest;
        if (batchRequest is not null)
        {
            batchRequest.Cancellation.Cancel();
            await batchRequest.Completion.Task.ConfigureAwait(true);
        }
        var classifiedExportRequest = activeClassifiedExportRequest;
        if (classifiedExportRequest is not null)
        {
            classifiedExportRequest.Cancellation.Cancel();
            await classifiedExportRequest.Completion.Task.ConfigureAwait(true);
        }
        if (IsExtracting)
        {
            if (!extractionCancellationRequested)
            {
                extractionCancellationRequested = true;
                IsCancellingExtraction = true;
                extractionCancellation?.Cancel();
            }
            await extractionCompletion.Task.ConfigureAwait(true);
        }
        var activeRequests = activePreviewRequests.ToArray();
        foreach (var request in activeRequests)
        {
            request.Cancellation.Cancel();
        }
        if (activeRequests.Length > 0)
        {
            await Task.WhenAll(activeRequests.Select(request => request.Completion.Task)).ConfigureAwait(true);
        }
        await CancelAndWaitClassifiedPreviewAsync().ConfigureAwait(true);
        await CancelAndWaitForSavesAsync().ConfigureAwait(true);
        discoveredArchives = Array.Empty<DiscoveredArchive>();
        SelectedDiscoveredArchive = null;
        classifiedImages = Array.Empty<ClassifiedImage>();
        selectedClassifiedImage = null;
        classifiedPreviewImage = null;
        gameRootPath = null;
        gameRootCoverageSummaryText = string.Empty;
        gameRootCoverageWarningText = string.Empty;
        NotifyGameRootProperties();
        Dispose();
    }

    private static TaskCompletionSource CompletedSource()
    {
        var source = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        source.SetResult();
        return source;
    }

    private sealed class DeclineCancellationConfirmationService : IExtractionCancellationConfirmationService
    {
        public bool ConfirmCancelExtraction() => false;
    }

    public IReadOnlySet<int> SelectedArchiveIndices => selectedArchiveIndices;

    public int SelectedCount => selectedArchiveIndices.Count;

    public void UpdateSelectedIndices(IEnumerable<int> indices)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (IsExtracting || IsBatchExportingPng) return;
        var valid = Entries.Select(entry => entry.Index).ToHashSet();
        selectedArchiveIndices = indices.Where(valid.Contains).ToHashSet();
        OnPropertyChanged(nameof(SelectedArchiveIndices));
        OnPropertyChanged(nameof(SelectedCount));
        OnPropertyChanged(nameof(ExtractionSelectionText));
        OnPropertyChanged(nameof(ExportSelectionText));
        OnPropertyChanged(nameof(CanExtractSelected));
        OnPropertyChanged(nameof(CanExport));
        ((AsyncCommand)ExtractSelectedCommand).RaiseCanExecuteChanged();
        ((AsyncCommand)ExportCommand).RaiseCanExecuteChanged();
    }

    public void SelectAllEntries()
    {
        UpdateSelectedIndices(Entries.Select(entry => entry.Index));
        SelectAllRequested?.Invoke(this, EventArgs.Empty);
    }

    public IReadOnlyList<int> FreezeSelectedArchiveIndices() =>
        Entries.Where(entry => selectedArchiveIndices.Contains(entry.Index)).Select(entry => entry.Index).ToArray();

    public IReadOnlyList<int> FreezeAllArchiveIndices() =>
        Entries.Select(entry => entry.Index).ToArray();

    public string ExtractionSelectionText => $"提取选中（{SelectedCount}）";

    public bool CanExtractSelected =>
        !IsLoading && !IsPreviewLoading && !IsExtracting && previewSession is not null && SelectedCount > 0 && SelectedCount < Entries.Count;

    public bool CanExtractAll =>
        !IsLoading && !IsPreviewLoading && !IsExtracting && previewSession is not null && Entries.Count > 0;

    public IReadOnlyList<ArchiveEntryProjection> Entries { get; private set; } = Array.Empty<ArchiveEntryProjection>();

    public ArchiveViewState State
    {
        get => state;
        private set => SetField(ref state, value);
    }

    public string StatusText
    {
        get => statusText;
        private set => SetField(ref statusText, value);
    }

    public string CurrentFileName
    {
        get => currentFileName ?? "未打开文件";
        private set
        {
            if (SetField(ref currentFileName, value))
            {
                OnPropertyChanged(nameof(CurrentFileName));
            }
        }
    }

    public bool IsLoading => State == ArchiveViewState.Loading;

    public ArchiveEntryProjection? SelectedEntry
    {
        get => selectedEntry;
        set
        {
            if (IsExtracting) return;
            if (SetField(ref selectedEntry, value))
            {
                if (!Equals(value, autoPreviewSuppressedEntry))
                {
                    autoPreviewSuppressedEntry = null;
                }
                CancelCurrentPreviewRequest();
                previewVersion++;
                PreviewImage = null;
                TransparencyHintText = string.Empty;
                previewSaveSnapshot = null;
                OnPropertyChanged(nameof(CanSavePreviewPng));
                IsUnsupportedPreviewSelection = value is not null && !IsPreviewCandidate(value);
                PreviewStatusText = value is null ? "请选择一个 MZP 条目。" : IsPreviewCandidate(value)
                    ? $"已选择：{value.Name}"
                    : "当前资源暂不支持图片预览。";
                isPreviewLoading = false;
                OnPropertyChanged(nameof(IsPreviewLoading));
                ((AsyncCommand)PreviewCommand).RaiseCanExecuteChanged();
                ((AsyncCommand)CancelPreviewCommand).RaiseCanExecuteChanged();
                if (value is not null && !Equals(value, autoPreviewSuppressedEntry) && IsPreviewCandidate(value) && previewSession is not null)
                {
                    var request = CreatePreviewRequest(value, PreviewPhase.PendingDelay);
                    currentPreviewRequest = request;
                    activePreviewRequests.Add(request);
                    isPreviewLoading = true;
                    OnPropertyChanged(nameof(IsPreviewLoading));
                    ((AsyncCommand)PreviewCommand).RaiseCanExecuteChanged();
                    ((AsyncCommand)CancelPreviewCommand).RaiseCanExecuteChanged();
                    _ = ScheduleAutoPreviewAsync(request);
                }
            }
        }
    }

    public System.Windows.Media.Imaging.BitmapSource? PreviewImage
    {
        get => previewImage;
        private set => SetField(ref previewImage, value);
    }

    public string PreviewStatusText
    {
        get => previewStatusText;
        private set => SetField(ref previewStatusText, value);
    }

    public string TransparencyHintText
    {
        get => transparencyHintText;
        private set => SetField(ref transparencyHintText, value);
    }

    public bool IsPreviewLoading => isPreviewLoading;

    public bool IsUnsupportedPreviewSelection
    {
        get => isUnsupportedPreviewSelection;
        private set => SetField(ref isUnsupportedPreviewSelection, value);
    }

    public async Task OpenAsync()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (IsExtracting || IsBatchExportingPng) return;
        var path = pendingTrustedOpenPath ?? filePicker.PickHfaFile();
        pendingTrustedOpenPath = null;
        if (path is null)
        {
            if (currentFileName is null && Entries.Count == 0)
            {
                State = ArchiveViewState.NotOpened;
            }

            StatusText = "已取消打开。源归档始终只读。";
            return;
        }

        openCancellation?.Cancel();
        openCancellation?.Dispose();
        openCancellation = new CancellationTokenSource();
        var batchRequest = activeBatchPngRequest;
        if (batchRequest is not null)
        {
            batchRequest.Cancellation.Cancel();
            await batchRequest.Completion.Task.ConfigureAwait(true);
        }
        var invalidatedRequests = InvalidatePreview();
        if (invalidatedRequests.Count > 0)
        {
            await Task.WhenAll(invalidatedRequests.Select(request => request.Completion.Task)).ConfigureAwait(true);
        }
        await CancelAndWaitForSavesAsync().ConfigureAwait(true);
        previewSession?.Dispose();
        previewSession = null;
        var token = openCancellation.Token;
        var version = ++operationVersion;
        State = ArchiveViewState.Loading;
        StatusText = "正在读取 HFA 条目……";
        CurrentFileName = Path.GetFileName(path);
        Entries = Array.Empty<ArchiveEntryProjection>();
        UpdateSelectedIndices(Array.Empty<int>());
        OnPropertyChanged(nameof(Entries));
        OnPropertyChanged(nameof(IsLoading));
        ((AsyncCommand)OpenCommand).RaiseCanExecuteChanged();

        IArchivePreviewSession? opened = null;
        try
        {
            opened = await Task.Run(() => archiveOpener.Open(path), token);
            if (token.IsCancellationRequested)
            {
                opened!.Dispose();
                opened = null;
                token.ThrowIfCancellationRequested();
            }
            if (version != operationVersion || disposed)
            {
                opened!.Dispose();
                opened = null;
                return;
            }

            var installedSession = opened!;
            previewSession = installedSession;
            var openedEntries = installedSession.Entries.OrderBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase).ToArray();
            opened = null;
            var entries = openedEntries;
            Entries = entries;
            SelectedEntry = null;
            IsUnsupportedPreviewSelection = false;
            PreviewStatusText = "请选择一个 MZP 条目。";
            CurrentFileName = Path.GetFileName(path);
            State = entries.Length == 0 ? ArchiveViewState.Empty : ArchiveViewState.Opened;
            OnPropertyChanged(nameof(CanExtractAll));
            OnPropertyChanged(nameof(CanExtractSelected));
            ((SyncCommand)SelectAllCommand).RaiseCanExecuteChanged();
            StatusText = entries.Length == 0
                ? "归档已打开，但其中没有条目。源归档未被修改。"
                : $"已打开，共 {entries.Length} 个条目。源归档未被修改。";
            OnPropertyChanged(nameof(Entries));
            SetExtractionCommandsEnabled(true);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            if (version == operationVersion && !disposed)
            {
                State = ArchiveViewState.Cancelled;
                StatusText = "打开操作已取消。源归档未被修改。";
            }
        }
        catch (VNAssetExplorer.Application.ApplicationException exception)
        {
            if (version == operationVersion && !disposed)
            {
                previewSession?.Dispose();
                previewSession = null;
                State = ArchiveViewState.Error;
                StatusText = MapError(exception.Code);
            }
        }
        catch (UnauthorizedAccessException)
        {
            if (version == operationVersion && !disposed)
            {
                previewSession?.Dispose();
                previewSession = null;
                State = ArchiveViewState.Error;
                StatusText = "没有权限读取该 HFA 文件。源归档未被修改。";
            }
        }
        catch (IOException)
        {
            if (version == operationVersion && !disposed)
            {
                previewSession?.Dispose();
                previewSession = null;
                State = ArchiveViewState.Error;
                StatusText = "读取 HFA 时发生 I/O 错误。源归档未被修改。";
            }
        }
        catch (Exception)
        {
            if (version == operationVersion && !disposed)
            {
                previewSession?.Dispose();
                previewSession = null;
                State = ArchiveViewState.Error;
                StatusText = "打开文件时发生内部错误。源归档未被修改。";
            }
        }
        finally
        {
            opened?.Dispose();
            if (version == operationVersion && !disposed)
            {
                OnPropertyChanged(nameof(IsLoading));
                ((AsyncCommand)OpenCommand).RaiseCanExecuteChanged();
                ((AsyncCommand)PreviewCommand).RaiseCanExecuteChanged();
            }
        }
    }

    public async Task SelectGameRootAsync()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        var selected = gameRootFolderPicker.PickGameRoot();
        if (selected is null) return;
        var previousScan = gameRootScanCompletion;
        gameRootScanCancellation?.Cancel();
        await previousScan.Task.ConfigureAwait(true);
        var previousImageClassification = imageClassificationCompletion;
        imageClassificationCancellation?.Cancel();
        await previousImageClassification.Task.ConfigureAwait(true);
        var previousAudioClassification = audioClassificationCompletion;
        audioClassificationCancellation?.Cancel();
        await previousAudioClassification.Task.ConfigureAwait(true);
        var previousVideoClassification = videoClassificationCompletion;
        videoClassificationCancellation?.Cancel();
        await previousVideoClassification.Task.ConfigureAwait(true);
        await CancelAndWaitClassifiedExportAsync().ConfigureAwait(true);
        await CancelAndWaitClassifiedPreviewAsync().ConfigureAwait(true);
        gameRootScanCancellation?.Dispose();
        gameRootScanCancellation = new CancellationTokenSource();
        gameRootScanCompletion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var token = gameRootScanCancellation.Token;
        string requestedRoot;
        try
        {
            requestedRoot = Path.GetFullPath(selected);
        }
        catch (Exception) when (selected is not null)
        {
            gameRootCoverageSummaryText = "扫描失败：所选游戏目录路径无效。";
            gameRootCoverageWarningText = "请重新选择存在且可读取的游戏目录。";
            gameRootScanCompletion.TrySetResult();
            NotifyGameRootProperties();
            return;
        }
        gameRootPath = requestedRoot;
        discoveredArchives = Array.Empty<DiscoveredArchive>();
        SelectedDiscoveredArchive = null;
        classifiedImages = Array.Empty<ClassifiedImage>();
        classifiedAudio = Array.Empty<ClassifiedAudio>();
        selectedClassifiedAudio = Array.Empty<ClassifiedAudio>();
        classifiedVideo = Array.Empty<ClassifiedVideo>();
        selectedClassifiedVideo = Array.Empty<ClassifiedVideo>();
        selectedClassifiedVideoFocus = null;
        isAudioMode = false;
        isVideoMode = false;
        selectedClassifiedImage = null;
        classifiedPreviewImage = null;
        imageClassificationStatusText = string.Empty;
        imageClassificationSummaryText = string.Empty;
        gameRootCoverageSummaryText = "正在扫描……";
        gameRootCoverageWarningText = "当前结果可能不包含游戏全部资源。";
        isGameRootScanning = true;
        NotifyGameRootProperties();
        OnPropertyChanged(nameof(ClassifiedImages));
        OnPropertyChanged(nameof(ClassifiedAudio));
        OnPropertyChanged(nameof(ClassifiedVideo));
        OnPropertyChanged(nameof(SelectedClassifiedAudioCount));
        OnPropertyChanged(nameof(SelectedClassifiedVideoFocus));
        OnPropertyChanged(nameof(SelectedClassifiedImage));
        OnPropertyChanged(nameof(ClassifiedPreviewImage));
        OnPropertyChanged(nameof(ImageClassificationStatusText));
        OnPropertyChanged(nameof(ImageClassificationSummaryText));
        OnPropertyChanged(nameof(AudioClassificationStatusText));
        OnPropertyChanged(nameof(VideoClassificationStatusText));
        OnPropertyChanged(nameof(IsAudioMode));
        OnPropertyChanged(nameof(IsVideoMode));
        OnPropertyChanged(nameof(ExportSelectionText));
        OnPropertyChanged(nameof(CanExport));
        ((AsyncCommand)ExportCommand).RaiseCanExecuteChanged();
        try
        {
            var result = await gameRootInventoryService.ScanAsync(requestedRoot, token);
            if (token.IsCancellationRequested || disposed || !Equals(gameRootPath, requestedRoot)) return;
            if (result.Outcome == GameRootInventoryOutcome.Error)
            {
                gameRootCoverageSummaryText = "扫描失败：无法读取所选游戏目录。没有发布完成的覆盖统计。";
                gameRootCoverageWarningText = "请确认目录存在且有读取权限。";
                return;
            }
            if (result.Outcome == GameRootInventoryOutcome.Cancelled)
            {
                gameRootCoverageSummaryText = "扫描已取消。";
                return;
            }
            discoveredArchives = result.Archives;
            var coverage = result.Coverage;
            gameRootCoverageSummaryText = $"扫描到普通文件：{coverage.ScannedFileCount}；确认 HFA 归档：{coverage.ConfirmedArchiveCount}；未识别普通文件：{coverage.UnrecognizedCount}；无法读取普通文件：{coverage.UnreadableCount}；无法读取子目录：{coverage.UnreadableDirectoryCount}；跳过 reparse 路径：{coverage.SkippedReparsePathCount}";
            gameRootCoverageWarningText = coverage.UnreadableDirectoryCount > 0
                ? "部分子目录无法读取，其中的文件未计入扫描文件数，当前结果可能不完整。请选择归档后再打开，不会自动打开第一个归档。"
                : "当前结果可能不包含游戏全部资源。请选择归档后再打开，不会自动打开第一个归档。";
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { return; }
        catch (UnauthorizedAccessException)
        {
            gameRootCoverageSummaryText = "扫描失败：没有权限读取所选游戏目录。";
            gameRootCoverageWarningText = "请重新选择有读取权限的目录。";
            return;
        }
        catch (IOException)
        {
            gameRootCoverageSummaryText = "扫描失败：读取所选游戏目录时发生 I/O 错误。";
            gameRootCoverageWarningText = "请确认目录和磁盘可用后重试。";
            return;
        }
        catch (Exception)
        {
            gameRootCoverageSummaryText = "扫描失败：内部错误。没有发布完成的覆盖统计。";
            gameRootCoverageWarningText = "请重新选择游戏目录后重试。";
            return;
        }
        finally
        {
            if (Equals(gameRootPath, requestedRoot))
            {
                isGameRootScanning = false;
                NotifyGameRootProperties();
            }
            gameRootScanCompletion.TrySetResult();
        }

        await EnterImagesAsync().ConfigureAwait(true);
    }

    public void SetClassifiedImagesForTesting(IReadOnlyList<ClassifiedImage> images)
    {
        classifiedImages = images;
        OnPropertyChanged(nameof(ClassifiedImages));
    }

    public void SetClassifiedAudioForTesting(IReadOnlyList<ClassifiedAudio> audio)
    {
        classifiedAudio = audio;
        selectedClassifiedAudio = Array.Empty<ClassifiedAudio>();
        selectedClassifiedAudioFocus = null;
        OnPropertyChanged(nameof(ClassifiedAudio));
        OnPropertyChanged(nameof(SelectedClassifiedAudioCount));
        OnPropertyChanged(nameof(SelectedClassifiedAudioFocus));
        OnPropertyChanged(nameof(ExportSelectionText));
        OnPropertyChanged(nameof(CanExport));
        ((AsyncCommand)ExportCommand).RaiseCanExecuteChanged();
    }

    public void SetClassifiedVideoForTesting(IReadOnlyList<ClassifiedVideo> videos)
    {
        classifiedVideo = videos;
        selectedClassifiedVideo = Array.Empty<ClassifiedVideo>();
        selectedClassifiedVideoFocus = null;
        OnPropertyChanged(nameof(ClassifiedVideo));
        OnPropertyChanged(nameof(SelectedClassifiedVideoFocus));
        OnPropertyChanged(nameof(ExportSelectionText));
        OnPropertyChanged(nameof(CanExport));
        ((AsyncCommand)ExportCommand).RaiseCanExecuteChanged();
    }

    public void UpdateSelectedClassifiedVideo(IEnumerable<ClassifiedVideo> videos)
    {
        selectedClassifiedVideo = videos.Where(classifiedVideo.Contains).Distinct().ToArray();
        OnPropertyChanged(nameof(ExportSelectionText));
        OnPropertyChanged(nameof(CanExport));
        ((AsyncCommand)ExportCommand).RaiseCanExecuteChanged();
    }

    public IReadOnlyList<ClassifiedVideo> FreezeSelectedClassifiedVideo() => classifiedVideo.Where(selectedClassifiedVideo.Contains).ToArray();

    public void SelectVideo(ClassifiedVideo video)
    {
        if (!classifiedVideo.Contains(video)) return;
        isAudioMode = false;
        isVideoMode = true;
        selectedClassifiedVideoFocus = video;
        PreviewImage = null;
        classifiedPreviewImage = null;
        IsUnsupportedPreviewSelection = true;
        PreviewStatusText = $"首版不提供应用内播放。原样资源可用；兼容 MP4 可用。{(video.HasAudio ? string.Empty : "此视频不含音轨。")}";
        OnPropertyChanged(nameof(IsAudioMode));
        OnPropertyChanged(nameof(IsVideoMode));
        OnPropertyChanged(nameof(SelectedClassifiedVideoFocus));
        OnPropertyChanged(nameof(ClassifiedPreviewImage));
        OnPropertyChanged(nameof(ExportSelectionText));
        OnPropertyChanged(nameof(CanExport));
        ((AsyncCommand)ExportCommand).RaiseCanExecuteChanged();
    }

    public async Task SelectClassifiedImageAsync(ClassifiedImage image)
    {
        if (disposed || !classifiedImages.Contains(image) || imageClassificationPreviewService is null) return;
        var request = new ClassifiedPreviewRequest(image, new CancellationTokenSource());
        lock (activeClassifiedPreviewRequests)
        {
            foreach (var active in activeClassifiedPreviewRequests) active.Cancellation.Cancel();
            activeClassifiedPreviewRequests.Add(request);
        }
        ClearClassifiedExportContext();
        selectedClassifiedImage = image;
        classifiedPreviewImage = null;
        imageClassificationStatusText = $"正在预览：{image.Name}";
        OnPropertyChanged(nameof(SelectedClassifiedImage));
        OnPropertyChanged(nameof(SelectedClassifiedImageCount));
        OnPropertyChanged(nameof(ClassifiedPreviewImage));
        OnPropertyChanged(nameof(ImageClassificationStatusText));
        try
        {
            await classifiedPreviewGate.WaitAsync(request.Cancellation.Token).ConfigureAwait(true);
            request.GateEntered = true;
            try
            {
                if (request.Cancellation.IsCancellationRequested || disposed || !Equals(selectedClassifiedImage, image)) return;
                try
                {
                    var preview = await imageClassificationPreviewService.PreviewAsync(image, request.Cancellation.Token).ConfigureAwait(true);
                    if (!disposed && !request.Cancellation.IsCancellationRequested && Equals(selectedClassifiedImage, image))
                    {
                        classifiedPreviewImage = CreateBitmap(preview.PngBytes);
                        SetClassifiedExportContext(image, preview.PngBytes);
                        imageClassificationStatusText = $"正在显示：{image.Name}";
                        OnPropertyChanged(nameof(ClassifiedPreviewImage));
                        OnPropertyChanged(nameof(ImageClassificationStatusText));
                    }
                }
                catch (OperationCanceledException) { }
                catch (VNAssetExplorer.Application.ApplicationException)
                {
                    if (!disposed && !request.Cancellation.IsCancellationRequested && Equals(selectedClassifiedImage, image))
                    {
                        classifiedPreviewImage = null;
                        imageClassificationStatusText = $"{image.Name} 当前无法预览。";
                        OnPropertyChanged(nameof(ClassifiedPreviewImage));
                        OnPropertyChanged(nameof(ImageClassificationStatusText));
                    }
                }
                catch (Exception)
                {
                    if (!disposed && !request.Cancellation.IsCancellationRequested && Equals(selectedClassifiedImage, image))
                    {
                        classifiedPreviewImage = null;
                        imageClassificationStatusText = $"{image.Name} 当前无法预览：内部错误。";
                        OnPropertyChanged(nameof(ClassifiedPreviewImage));
                        OnPropertyChanged(nameof(ImageClassificationStatusText));
                    }
                }
            }
            finally
            {
                classifiedPreviewGate.Release();
                request.GateEntered = false;
            }
        }
        catch (OperationCanceledException) { }
        finally
        {
            request.Completion.TrySetResult();
            lock (activeClassifiedPreviewRequests) activeClassifiedPreviewRequests.Remove(request);
            request.Cancellation.Dispose();
        }
    }

    private async Task CancelAndWaitClassifiedPreviewAsync()
    {
        ClassifiedPreviewRequest[] snapshot;
        lock (activeClassifiedPreviewRequests) snapshot = activeClassifiedPreviewRequests.ToArray();
        foreach (var request in snapshot) request.Cancellation.Cancel();
        if (snapshot.Length > 0) await Task.WhenAll(snapshot.Select(request => request.Completion.Task)).ConfigureAwait(true);
    }

    public void UpdateSelectedClassifiedImages(IEnumerable<ClassifiedImage> images)
    {
        selectedClassifiedImages = images.Where(classifiedImages.Contains).Distinct().ToArray();
        OnPropertyChanged(nameof(SelectedClassifiedImageCount));
        OnPropertyChanged(nameof(ExportSelectionText));
        OnPropertyChanged(nameof(CanExport));
        ((AsyncCommand)ExportCommand).RaiseCanExecuteChanged();
    }

    public int SelectedClassifiedAudioCount => selectedClassifiedAudio.Count;

    public void UpdateSelectedClassifiedAudio(IEnumerable<ClassifiedAudio> audio)
    {
        selectedClassifiedAudio = audio.Where(classifiedAudio.Contains).Distinct().ToArray();
        OnPropertyChanged(nameof(SelectedClassifiedAudioCount));
        OnPropertyChanged(nameof(ExportSelectionText));
        OnPropertyChanged(nameof(CanExport));
        ((AsyncCommand)ExportCommand).RaiseCanExecuteChanged();
    }

    public IReadOnlyList<ClassifiedAudio> FreezeSelectedClassifiedAudio() =>
        classifiedAudio.Where(selectedClassifiedAudio.Contains).ToArray();

    public void SelectClassifiedImage(ClassifiedImage image)
    {
        if (!classifiedImages.Contains(image)) return;
        _ = SelectClassifiedImageAsync(image);
    }

    public void SortClassifiedImages(ImageSortKey key, ImageSortDirection direction)
    {
        var current = selectedClassifiedImage;
        classifiedImages = ImageClassificationOrdering.Sort(classifiedImages, key, direction);
        imageSortKey = key;
        imageSortDirection = direction;
        selectedClassifiedImage = current is not null && classifiedImages.Contains(current) ? current : null;
        OnPropertyChanged(nameof(ClassifiedImages));
        OnPropertyChanged(nameof(SelectedClassifiedImage));
    }

    public void SortCurrentClassifiedMedia(ImageSortKey key, ImageSortDirection direction)
    {
        if (IsVideoMode)
        {
            var videoFocus = selectedClassifiedVideoFocus;
            classifiedVideo = SortClassifiedVideo(classifiedVideo, key, direction);
            imageSortKey = key;
            imageSortDirection = direction;
            selectedClassifiedVideoFocus = videoFocus is not null && classifiedVideo.Contains(videoFocus) ? videoFocus : null;
            OnPropertyChanged(nameof(ClassifiedVideo));
            OnPropertyChanged(nameof(SelectedClassifiedVideoFocus));
            return;
        }
        if (!IsAudioMode)
        {
            SortClassifiedImages(key, direction);
            return;
        }

        var current = selectedClassifiedAudioFocus;
        classifiedAudio = SortClassifiedAudio(classifiedAudio, key, direction);
        imageSortKey = key;
        imageSortDirection = direction;
        selectedClassifiedAudioFocus = current is not null && classifiedAudio.Contains(current) ? current : null;
        OnPropertyChanged(nameof(ClassifiedAudio));
        OnPropertyChanged(nameof(SelectedClassifiedAudioFocus));
    }

    public void MoveClassifiedImage(int delta)
    {
        if (classifiedImages.Count == 0) return;
        var index = selectedClassifiedImage is null ? -1 : classifiedImages.ToList().IndexOf(selectedClassifiedImage);
        var next = Math.Clamp(index + delta, 0, classifiedImages.Count - 1);
        _ = SelectClassifiedImageAsync(classifiedImages[next]);
    }

    public void MoveCurrentClassifiedMedia(int delta)
    {
        if (IsVideoMode)
        {
            if (classifiedVideo.Count == 0) return;
            var videoIndex = selectedClassifiedVideoFocus is null ? -1 : classifiedVideo.ToList().IndexOf(selectedClassifiedVideoFocus);
            SelectVideo(classifiedVideo[Math.Clamp(videoIndex + delta, 0, classifiedVideo.Count - 1)]);
            return;
        }
        if (!IsAudioMode)
        {
            MoveClassifiedImage(delta);
            return;
        }

        if (classifiedAudio.Count == 0) return;
        var index = selectedClassifiedAudioFocus is null ? -1 : classifiedAudio.ToList().IndexOf(selectedClassifiedAudioFocus);
        SelectAudio(classifiedAudio[Math.Clamp(index + delta, 0, classifiedAudio.Count - 1)]);
    }

    public async Task EnterImagesAsync()
    {
        if (disposed || gameRootPath is null || isGameRootScanning || isClassifyingImages) return;
        var root = gameRootPath;
        await CancelAndWaitClassifiedExportAsync().ConfigureAwait(true);
        if (isAudioMode || isVideoMode)
        {
            isAudioMode = false;
            isVideoMode = false;
            OnPropertyChanged(nameof(IsAudioMode));
            OnPropertyChanged(nameof(IsVideoMode));
            OnPropertyChanged(nameof(ExportSelectionText));
            OnPropertyChanged(nameof(CanExport));
            ((AsyncCommand)ExportCommand).RaiseCanExecuteChanged();
        }
        var previousAudioClassification = currentAudioClassificationRequest;
        if (previousAudioClassification is not null)
        {
            previousAudioClassification.Cancellation.Cancel();
            await previousAudioClassification.Completion.Task.ConfigureAwait(true);
        }
        var previousVideoClassification = currentVideoClassificationRequest;
        if (previousVideoClassification is not null)
        {
            previousVideoClassification.Cancellation.Cancel();
            await previousVideoClassification.Completion.Task.ConfigureAwait(true);
        }
        if (disposed || !Equals(root, gameRootPath)) return;
        classifiedPreviewImage = null;
        IsUnsupportedPreviewSelection = false;
        PreviewStatusText = "请选择一个图片资源。";
        OnPropertyChanged(nameof(ClassifiedPreviewImage));
        var inventory = new GameRootInventoryResult(root, discoveredArchives, new(0, discoveredArchives.Count, 0, 0, 0, 0), GameRootInventoryOutcome.Completed);
        imageClassificationCancellation?.Dispose();
        imageClassificationCancellation = new CancellationTokenSource();
        imageClassificationCompletion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        isClassifyingImages = true;
        imageClassificationStatusText = "正在识别图片……";
        imageClassificationSummaryText = imageClassificationStatusText;
        OnPropertyChanged(nameof(ImageClassificationSummaryText));
        NotifyImageClassificationProperties();
        try
        {
            var progress = new Progress<ImageClassificationProgress>(value =>
            {
                if (!disposed && isClassifyingImages && Equals(root, gameRootPath))
                {
                    imageClassificationStatusText = $"正在识别图片：{value.Position}/{value.Total}，已确认 {value.ConfirmedImages} 张。";
                    imageClassificationSummaryText = imageClassificationStatusText;
                    OnPropertyChanged(nameof(ImageClassificationStatusText));
                    OnPropertyChanged(nameof(ImageClassificationSummaryText));
                }
            });
            var batches = new Progress<ImageClassificationBatch>(batch =>
            {
                if (!disposed && isClassifyingImages && Equals(root, gameRootPath) && batch.Images.Count > 0)
                {
                    classifiedImages = classifiedImages.Concat(batch.Images).ToArray();
                    OnPropertyChanged(nameof(ClassifiedImages));
                }
            });
            ImageClassificationResult result;
            if (imageClassificationService is VNAssetExplorer.App.Services.IImageClassificationBatchService batchService)
                result = await batchService.ClassifyAsync(inventory, imageClassificationCancellation.Token, progress, batches);
            else
                result = await imageClassificationService.ClassifyAsync(inventory, imageClassificationCancellation.Token, progress);
            if (disposed || !Equals(root, gameRootPath)) return;
            classifiedImages = result.Images;
            imageClassificationStatusText = result.IsComplete
                ? $"图片识别完成：{result.Images.Count} 张已确认图片。"
                : $"图片识别已取消，当前显示 {result.Images.Count} 张已确认图片，结果不完整。";
            imageClassificationSummaryText = imageClassificationStatusText;
            OnPropertyChanged(nameof(ClassifiedImages));
            OnPropertyChanged(nameof(ImageClassificationStatusText));
            OnPropertyChanged(nameof(ImageClassificationSummaryText));
        }
        catch (OperationCanceledException) when (imageClassificationCancellation?.IsCancellationRequested == true)
        {
            if (!disposed && Equals(root, gameRootPath))
            {
                imageClassificationStatusText = $"图片识别已取消，当前显示 {classifiedImages.Count} 张已确认图片，结果不完整。";
                imageClassificationSummaryText = imageClassificationStatusText;
                OnPropertyChanged(nameof(ImageClassificationStatusText));
                OnPropertyChanged(nameof(ImageClassificationSummaryText));
            }
        }
        catch (VNAssetExplorer.Application.ApplicationException exception)
        {
            if (!disposed && Equals(root, gameRootPath))
            {
                imageClassificationStatusText = $"图片识别失败：{exception.Message}";
                imageClassificationSummaryText = imageClassificationStatusText;
                OnPropertyChanged(nameof(ImageClassificationStatusText));
                OnPropertyChanged(nameof(ImageClassificationSummaryText));
            }
        }
        catch (Exception)
        {
            if (!disposed && Equals(root, gameRootPath))
            {
                imageClassificationStatusText = "图片识别失败：内部错误。当前已确认结果保留，结果可能不完整。";
                imageClassificationSummaryText = imageClassificationStatusText;
                OnPropertyChanged(nameof(ImageClassificationStatusText));
                OnPropertyChanged(nameof(ImageClassificationSummaryText));
            }
        }
        finally
        {
            isClassifyingImages = false;
            imageClassificationCompletion.TrySetResult();
            NotifyImageClassificationProperties();
        }
    }

    public async Task CancelImageClassificationAsync()
    {
        var completion = imageClassificationCompletion;
        imageClassificationCancellation?.Cancel();
        await completion.Task.ConfigureAwait(true);
    }

    public async Task EnterVideoAsync()
    {
        if (disposed || gameRootPath is null || isGameRootScanning || isClassifyingVideo) return;
        var root = gameRootPath;
        await CancelAndWaitClassifiedExportAsync().ConfigureAwait(true);
        var previousAudioClassification = currentAudioClassificationRequest;
        if (previousAudioClassification is not null)
        {
            previousAudioClassification.Cancellation.Cancel();
            await previousAudioClassification.Completion.Task.ConfigureAwait(true);
        }
        if (disposed || !Equals(root, gameRootPath)) return;
        isAudioMode = false;
        isVideoMode = true;
        OnPropertyChanged(nameof(IsAudioMode));
        OnPropertyChanged(nameof(IsVideoMode));
        var request = new VideoClassificationRequest(root, new CancellationTokenSource());
        currentVideoClassificationRequest = request;
        videoClassificationCancellation = request.Cancellation;
        videoClassificationCompletion = request.Completion;
        isClassifyingVideo = true;
        videoClassificationStatusText = "正在识别视频……";
        OnPropertyChanged(nameof(IsClassifyingVideo));
        OnPropertyChanged(nameof(VideoClassificationStatusText));
        try
        {
            var inventory = new GameRootInventoryResult(root, discoveredArchives, new(0, discoveredArchives.Count, 0, 0, 0, 0), GameRootInventoryOutcome.Completed);
            var result = await videoClassificationService.ClassifyAsync(inventory, request.Cancellation.Token).ConfigureAwait(true);
            if (!disposed && isVideoMode && Equals(root, gameRootPath) && ReferenceEquals(currentVideoClassificationRequest, request))
            {
                classifiedVideo = result.Videos;
                videoClassificationStatusText = result.IsComplete
                    ? $"视频识别完成：已扫描 {result.ScannedEntryCount} 项，确认 MP4 / H.264 {result.Videos.Count} 项，不支持 {result.UnsupportedEntryCount} 项，失败 {result.FailedEntryCount} 项。"
                    : $"视频识别已取消：当前显示 {result.Videos.Count} 项已确认 MP4 / H.264。结果不完整。";
                OnPropertyChanged(nameof(ClassifiedVideo));
                OnPropertyChanged(nameof(VideoClassificationStatusText));
            }
        }
        catch (OperationCanceledException) when (request.Cancellation.IsCancellationRequested) { }
        catch (Exception) when (!disposed && ReferenceEquals(currentVideoClassificationRequest, request))
        {
            videoClassificationStatusText = "视频识别失败：当前已确认结果保留，结果可能不完整。";
            OnPropertyChanged(nameof(VideoClassificationStatusText));
        }
        finally
        {
            request.Completion.TrySetResult();
            if (ReferenceEquals(currentVideoClassificationRequest, request))
            {
                isClassifyingVideo = false;
                OnPropertyChanged(nameof(IsClassifyingVideo));
            }
        }
    }

    public async Task EnterAudioAsync()
    {
        if (disposed || gameRootPath is null || isGameRootScanning || isClassifyingAudio) return;
        var root = gameRootPath;
        await CancelAndWaitClassifiedExportAsync().ConfigureAwait(true);
        var previousVideoClassification = currentVideoClassificationRequest;
        if (previousVideoClassification is not null)
        {
            previousVideoClassification.Cancellation.Cancel();
            await previousVideoClassification.Completion.Task.ConfigureAwait(true);
        }
        if (disposed || !Equals(root, gameRootPath)) return;
        isAudioMode = true;
        isVideoMode = false;
        OnPropertyChanged(nameof(IsAudioMode));
        OnPropertyChanged(nameof(IsVideoMode));
        await CancelAndWaitClassifiedPreviewAsync().ConfigureAwait(true);
        if (disposed || !Equals(root, gameRootPath) || !isAudioMode) return;
        classifiedPreviewImage = null;
        ClearClassifiedExportContext();
        IsUnsupportedPreviewSelection = false;
        PreviewStatusText = string.Empty;
        OnPropertyChanged(nameof(ClassifiedPreviewImage));
        OnPropertyChanged(nameof(ExportSelectionText));
        OnPropertyChanged(nameof(CanExport));
        ((AsyncCommand)ExportCommand).RaiseCanExecuteChanged();
        var inventory = new GameRootInventoryResult(root, discoveredArchives, new(0, discoveredArchives.Count, 0, 0, 0, 0), GameRootInventoryOutcome.Completed);
        audioClassificationCancellation?.Dispose();
        audioClassificationCancellation = new CancellationTokenSource();
        var request = new AudioClassificationRequest(root, audioClassificationCancellation);
        currentAudioClassificationRequest = request;
        audioClassificationCompletion = request.Completion;
        isClassifyingAudio = true;
        audioClassificationStatusText = "正在识别音频……";
        NotifyAudioClassificationProperties();
        try
        {
            var result = await audioClassificationService.ClassifyAsync(inventory, request.Cancellation.Token).ConfigureAwait(true);
            if (!CanPublishAudioClassification(request)) return;
            classifiedAudio = result.Audio;
            audioClassificationStatusText = result.IsComplete
                ? $"音频识别完成：已扫描 {result.ScannedEntryCount} 项，确认 Ogg Vorbis {result.Audio.Count} 项，不支持 {result.UnsupportedEntryCount} 项，失败 {result.FailedEntryCount} 项。"
                : $"音频识别已取消：已扫描 {result.ScannedEntryCount} 项，确认 Ogg Vorbis {result.Audio.Count} 项，不支持 {result.UnsupportedEntryCount} 项，失败 {result.FailedEntryCount} 项。结果不完整。";
            OnPropertyChanged(nameof(ClassifiedAudio));
            OnPropertyChanged(nameof(AudioClassificationStatusText));
        }
        catch (OperationCanceledException) when (request.Cancellation.IsCancellationRequested)
        {
            if (CanPublishAudioClassification(request))
            {
                audioClassificationStatusText = $"音频识别已取消，当前显示 {classifiedAudio.Count} 项已确认 Ogg Vorbis。结果不完整。";
                OnPropertyChanged(nameof(AudioClassificationStatusText));
            }
        }
        catch (VNAssetExplorer.Application.ApplicationException exception)
        {
            if (CanPublishAudioClassification(request))
            {
                audioClassificationStatusText = $"音频识别失败：{exception.Message}";
                OnPropertyChanged(nameof(AudioClassificationStatusText));
            }
        }
        catch (Exception)
        {
            if (CanPublishAudioClassification(request))
            {
                audioClassificationStatusText = "音频识别失败：内部错误。当前已确认结果保留，结果可能不完整。";
                OnPropertyChanged(nameof(AudioClassificationStatusText));
            }
        }
        finally
        {
            request.Completion.TrySetResult();
            if (ReferenceEquals(currentAudioClassificationRequest, request))
            {
                isClassifyingAudio = false;
                NotifyAudioClassificationProperties();
            }
        }
    }

    public async Task CancelAudioClassificationAsync()
    {
        var request = currentAudioClassificationRequest;
        request?.Cancellation.Cancel();
        if (request is not null) await request.Completion.Task.ConfigureAwait(true);
    }

    public void SelectAudio(ClassifiedAudio audio)
    {
        if (!classifiedAudio.Contains(audio)) return;
        var enteredAudioMode = false;
        if (!isAudioMode)
        {
            isAudioMode = true;
            enteredAudioMode = true;
            OnPropertyChanged(nameof(IsAudioMode));
        }
        selectedClassifiedAudioFocus = audio;
        PreviewImage = null;
        classifiedPreviewImage = null;
        IsUnsupportedPreviewSelection = true;
        PreviewStatusText = "当前资源不属于图片资源，无法预览。";
        OnPropertyChanged(nameof(SelectedClassifiedAudioFocus));
        OnPropertyChanged(nameof(ClassifiedPreviewImage));
        if (enteredAudioMode)
        {
            OnPropertyChanged(nameof(ExportSelectionText));
            OnPropertyChanged(nameof(CanExport));
            ((AsyncCommand)ExportCommand).RaiseCanExecuteChanged();
        }
    }

    private static IReadOnlyList<ClassifiedAudio> SortClassifiedAudio(IEnumerable<ClassifiedAudio> audio, ImageSortKey key, ImageSortDirection direction)
    {
        var items = audio.ToArray();
        IOrderedEnumerable<ClassifiedAudio> ordered = key switch
        {
            ImageSortKey.Name when direction == ImageSortDirection.Ascending => items.OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase),
            ImageSortKey.Name => items.OrderByDescending(item => item.Name, StringComparer.OrdinalIgnoreCase),
            ImageSortKey.Type when direction == ImageSortDirection.Ascending => items.OrderBy(item => item.Type, StringComparer.OrdinalIgnoreCase),
            ImageSortKey.Type => items.OrderByDescending(item => item.Type, StringComparer.OrdinalIgnoreCase),
            ImageSortKey.Size when direction == ImageSortDirection.Ascending => items.OrderBy(item => item.Size),
            ImageSortKey.Size => items.OrderByDescending(item => item.Size),
            ImageSortKey.ResourceOrder when direction == ImageSortDirection.Ascending => items.OrderBy(item => item.ResourceOrdinal),
            _ => items.OrderByDescending(item => item.ResourceOrdinal),
        };
        return (direction == ImageSortDirection.Ascending
                ? ordered.ThenBy(item => item.ResourceOrdinal)
                : ordered.ThenByDescending(item => item.ResourceOrdinal))
            .ToArray();
    }

    private static IReadOnlyList<ClassifiedVideo> SortClassifiedVideo(IEnumerable<ClassifiedVideo> video, ImageSortKey key, ImageSortDirection direction)
    {
        var items = video.ToArray();
        IOrderedEnumerable<ClassifiedVideo> ordered = key switch
        {
            ImageSortKey.Name when direction == ImageSortDirection.Ascending => items.OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase),
            ImageSortKey.Name => items.OrderByDescending(item => item.Name, StringComparer.OrdinalIgnoreCase),
            ImageSortKey.Type when direction == ImageSortDirection.Ascending => items.OrderBy(item => item.Codec, StringComparer.OrdinalIgnoreCase),
            ImageSortKey.Type => items.OrderByDescending(item => item.Codec, StringComparer.OrdinalIgnoreCase),
            ImageSortKey.Size when direction == ImageSortDirection.Ascending => items.OrderBy(item => item.Size),
            ImageSortKey.Size => items.OrderByDescending(item => item.Size),
            ImageSortKey.ResourceOrder when direction == ImageSortDirection.Ascending => items.OrderBy(item => item.ResourceOrdinal),
            _ => items.OrderByDescending(item => item.ResourceOrdinal),
        };
        return (direction == ImageSortDirection.Ascending
                ? ordered.ThenBy(item => item.ResourceOrdinal)
                : ordered.ThenByDescending(item => item.ResourceOrdinal))
            .ToArray();
    }

    private void NotifyAudioClassificationProperties() => OnPropertyChanged(nameof(IsClassifyingAudio));

    private bool CanPublishAudioClassification(AudioClassificationRequest request) =>
        !disposed &&
        isAudioMode &&
        Equals(request.Root, gameRootPath) &&
        ReferenceEquals(currentAudioClassificationRequest, request);

    private void NotifyImageClassificationProperties()
    {
        OnPropertyChanged(nameof(IsClassifyingImages));
        OnPropertyChanged(nameof(CanCancelImageClassification));
    }

    private void NotifyGameRootProperties()
    {
        OnPropertyChanged(nameof(GameRootPath));
        OnPropertyChanged(nameof(DiscoveredArchives));
        OnPropertyChanged(nameof(GameRootCoverageSummaryText));
        OnPropertyChanged(nameof(GameRootCoverageWarningText));
        OnPropertyChanged(nameof(IsGameRootScanning));
        ((AsyncCommand)SelectGameRootCommand).RaiseCanExecuteChanged();
        ((AsyncCommand)OpenDiscoveredArchiveCommand).RaiseCanExecuteChanged();
        ((AsyncCommand)CancelGameRootScanCommand).RaiseCanExecuteChanged();
        ((AsyncCommand)OpenCommand).RaiseCanExecuteChanged();
    }

    public async Task CancelGameRootScanAsync()
    {
        var completion = gameRootScanCompletion;
        gameRootScanCancellation?.Cancel();
        await completion.Task.ConfigureAwait(true);
    }

    public async Task OpenDiscoveredArchiveAsync()
    {
        var archive = SelectedDiscoveredArchive;
        if (archive is null || isGameRootScanning || disposed) return;
        pendingTrustedOpenPath = archive.FullPath;
        await OpenAsync();
    }

    public Task PreviewAsync() => StartManualPreviewAsync();

    private Task StartManualPreviewAsync()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        var session = previewSession;
        var entry = SelectedEntry;
        if (session is null || entry is null || IsExtracting) return Task.CompletedTask;

        CancelCurrentPreviewRequest();
        var request = CreatePreviewRequest(entry, PreviewPhase.RunningPreview, session);
        currentPreviewRequest = request;
        activePreviewRequests.Add(request);
        PreviewImage = null;
        TransparencyHintText = string.Empty;
        previewSaveSnapshot = null;
        OnPropertyChanged(nameof(CanSavePreviewPng));
        isPreviewLoading = true;
        OnPropertyChanged(nameof(IsPreviewLoading));
        PreviewStatusText = "正在生成预览……";
        RaisePreviewCommands();
        return PreviewEntryAsync(request);
    }

    private PreviewRequest CreatePreviewRequest(ArchiveEntryProjection entry, PreviewPhase phase, IArchiveSession? session = null)
    {
        var request = new PreviewRequest(++previewVersion, session ?? previewSession!, entry, new CancellationTokenSource());
        request.Phase = phase;
        return request;
    }

    private async Task PreviewEntryAsync(PreviewRequest request)
    {
        var gateEntered = false;
        try
        {
            await previewSessionGate.WaitAsync(request.Cancellation.Token);
            gateEntered = true;
            request.Phase = PreviewPhase.RunningPreview;
            var result = await request.Session.PreviewAsync(request.Entry.Index, request.Cancellation.Token);
            request.Cancellation.Token.ThrowIfCancellationRequested();
            if (IsCurrent(request))
            {
                PreviewImage = CreateBitmap(result.PngBytes);
                TransparencyHintText = HasTransparency(PreviewImage)
                    ? "图片包含透明区域；预览使用白色背景，其他看图软件中外观可能不同。"
                    : string.Empty;
                previewSaveSnapshot = new PreviewSaveSnapshot(request.Session, request.Entry, result.PngBytes.ToArray());
                PreviewStatusText = $"预览成功：{result.Width} × {result.Height}。源归档未被修改。";
                OnPropertyChanged(nameof(CanSavePreviewPng));
            }
        }
        catch (OperationCanceledException)
        {
            if (IsCurrent(request))
            {
                previewSaveSnapshot = null;
                TransparencyHintText = string.Empty;
                OnPropertyChanged(nameof(CanSavePreviewPng));
            }
            if (IsCurrent(request) && request.Cancellation.IsCancellationRequested)
                PreviewStatusText = "预览操作已取消。源归档未被修改。";
        }
        catch (VNAssetExplorer.Application.ApplicationException exception)
        {
            if (IsCurrent(request))
            {
                previewSaveSnapshot = null;
                TransparencyHintText = string.Empty;
                OnPropertyChanged(nameof(CanSavePreviewPng));
                PreviewStatusText = MapPreviewError(exception.Code);
            }
        }
        catch (Exception)
        {
            if (IsCurrent(request))
            {
                previewSaveSnapshot = null;
                TransparencyHintText = string.Empty;
                OnPropertyChanged(nameof(CanSavePreviewPng));
                PreviewStatusText = "生成预览时发生内部错误。源归档未被修改。";
            }
        }
        finally
        {
            if (gateEntered) previewSessionGate.Release();
            request.Completion.TrySetResult();
            activePreviewRequests.Remove(request);
            if (IsCurrent(request))
            {
                currentPreviewRequest = null;
                isPreviewLoading = false;
                OnPropertyChanged(nameof(IsPreviewLoading));
                RaisePreviewCommands();
            }
            if (disposed && !IsExtracting && activePreviewRequests.Count == 0 && ReferenceEquals(request.Session, previewSession))
            {
                request.Session.Dispose();
                previewSession = null;
            }
        }
    }

    private async Task ScheduleAutoPreviewAsync(PreviewRequest request)
    {
        try
        {
            await autoPreviewDelay.WaitAsync(TimeSpan.FromMilliseconds(150), request.Cancellation.Token);
            if (!IsCurrent(request))
            {
                request.Completion.TrySetResult();
                activePreviewRequests.Remove(request);
                return;
            }
            request.Phase = PreviewPhase.RunningPreview;
            await PreviewEntryAsync(request);
        }
        catch (OperationCanceledException)
        {
            request.Completion.TrySetResult();
            activePreviewRequests.Remove(request);
            if (IsCurrent(request))
            {
                currentPreviewRequest = null;
                isPreviewLoading = false;
                OnPropertyChanged(nameof(IsPreviewLoading));
                RaisePreviewCommands();
            }
        }
        catch (Exception)
        {
            request.Completion.TrySetResult();
            activePreviewRequests.Remove(request);
            if (IsCurrent(request))
            {
                currentPreviewRequest = null;
                isPreviewLoading = false;
                OnPropertyChanged(nameof(IsPreviewLoading));
                RaisePreviewCommands();
            }
        }
    }

    private bool IsCurrent(PreviewRequest request) =>
        !disposed && ReferenceEquals(currentPreviewRequest, request) && request.Version == previewVersion &&
        ReferenceEquals(request.Session, previewSession) && Equals(request.Entry, SelectedEntry);

    private void CancelCurrentPreviewRequest()
    {
        currentPreviewRequest?.Cancellation.Cancel();
    }

    private void RaisePreviewCommands()
    {
        ((AsyncCommand)PreviewCommand).RaiseCanExecuteChanged();
        ((AsyncCommand)CancelPreviewCommand).RaiseCanExecuteChanged();
        ((AsyncCommand)SavePreviewPngCommand).RaiseCanExecuteChanged();
        ((AsyncCommand)ExportCommand).RaiseCanExecuteChanged();
        OnPropertyChanged(nameof(CanExport));
    }

    public Task CancelPreviewAsync()
    {
        autoPreviewSuppressedEntry = SelectedEntry;
        CancelCurrentPreviewRequest();
        if (SelectedEntry is not null)
        {
            isPreviewLoading = false;
            previewSaveSnapshot = null;
            TransparencyHintText = string.Empty;
            OnPropertyChanged(nameof(CanSavePreviewPng));
            PreviewStatusText = "预览操作已取消。源归档未被修改。";
            OnPropertyChanged(nameof(IsPreviewLoading));
            RaisePreviewCommands();
        }
        return Task.CompletedTask;
    }

    public async Task SavePreviewPngAsync()
    {
        if (!HasValidPreviewSaveSnapshot)
        {
            previewSaveStatusText = "当前选择尚未生成可保存的预览 PNG。请先预览可转换图片。";
            OnPropertyChanged(nameof(PreviewSaveStatusText));
            return;
        }
        if (!CanSavePreviewPng || previewSaveSnapshot is null) return;
        var snapshot = previewSaveSnapshot;
        var target = previewPngSaveDialog.PickSaveTarget(PreviewPngFilename.Suggest(snapshot.Entry.Name));
        if (target is null)
        {
            previewSaveStatusText = "已取消保存预览。未写入文件。";
            OnPropertyChanged(nameof(PreviewSaveStatusText));
            return;
        }

        var request = new SaveRequest(snapshot, target, new CancellationTokenSource());
        activeSaveRequests.Add(request);
        isSavingPreviewPng = true;
        OnPropertyChanged(nameof(IsSavingPreviewPng));
        OnPropertyChanged(nameof(CanSavePreviewPng));
        try
        {
            await previewPngWriter.WriteAsync(target.FullPath, snapshot.PngBytes, target.AllowOverwrite, request.Cancellation.Token);
            previewSaveStatusText = $"预览 PNG 已保存：{Path.GetFileName(target.FullPath)}";
        }
        catch (OperationCanceledException)
        {
            previewSaveStatusText = "已取消保存预览。未写入文件。";
        }
        catch (UnauthorizedAccessException)
        {
            previewSaveStatusText = "没有权限写入所选位置。未修改源归档。";
        }
        catch (ArgumentException)
        {
            previewSaveStatusText = "所选保存位置无效。未写入文件。";
        }
        catch (IOException)
        {
            previewSaveStatusText = "保存 PNG 时发生 I/O 错误。未完成文件已清理。";
        }
        catch (Exception)
        {
            previewSaveStatusText = "保存 PNG 时发生内部错误。未完成文件已清理。";
        }
        finally
        {
            activeSaveRequests.Remove(request);
            request.Cancellation.Dispose();
            request.Completion.TrySetResult();
            isSavingPreviewPng = false;
            OnPropertyChanged(nameof(IsSavingPreviewPng));
            OnPropertyChanged(nameof(CanSavePreviewPng));
            OnPropertyChanged(nameof(PreviewSaveStatusText));
            PublishMainExportStatus(previewSaveStatusText);
        }
    }

    private async Task CancelAndWaitForSavesAsync()
    {
        var requests = activeSaveRequests.ToArray();
        foreach (var request in requests) request.Cancellation.Cancel();
        if (requests.Length > 0)
        {
            await Task.WhenAll(requests.Select(request => request.Completion.Task)).ConfigureAwait(true);
        }
    }

    private async Task ExportClassifiedImagesPngAsync(IReadOnlyList<ClassifiedImage> images)
    {
        var outputFolder = batchPngOutputFolderPicker.PickOutputFolder(recentOutputParentDirectory);
        if (outputFolder is null) return;
        Directory.CreateDirectory(outputFolder);
        var request = CreateClassifiedExportRequest();
        activeClassifiedExportRequest = request;
        isBatchExportingPng = true;
        isCancellingBatchExportPng = false;
        batchExportSummaryText = string.Empty;
        OnPropertyChanged(nameof(BatchExportSummaryText));
        RaiseBatchCommands();
        var reserved = Directory.EnumerateFileSystemEntries(outputFolder)
            .Select(Path.GetFileName)
            .Where(name => name is not null)
            .Cast<string>()
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var written = 0;
        var failed = 0;
        var cancelled = 0;
        try
        {
            foreach (var image in images)
            {
                try
                {
                    request.Cancellation.Token.ThrowIfCancellationRequested();
                    var preview = await imageClassificationPreviewService!.PreviewAsync(image, request.Cancellation.Token).ConfigureAwait(true);
                    var fileName = ReserveClassifiedPngName(image.Name, reserved);
                    await previewPngWriter.WriteAsync(Path.Combine(outputFolder, fileName), preview.PngBytes.ToArray(), false, request.Cancellation.Token).ConfigureAwait(true);
                    written++;
                }
                catch (OperationCanceledException) when (request.Cancellation.IsCancellationRequested)
                {
                    cancelled = images.Count - written - failed;
                    break;
                }
                catch (Exception) { failed++; }
            }
            batchExportSummaryText = $"导出完成：{written} 张成功，{failed} 张失败，{cancelled} 张取消。\n输出目录：{outputFolder}";
            OnPropertyChanged(nameof(BatchExportSummaryText));
            PublishMainExportStatus(batchExportSummaryText);
        }
        finally
        {
            request.Completion.TrySetResult();
            request.Cancellation.Dispose();
            if (ReferenceEquals(activeClassifiedExportRequest, request)) activeClassifiedExportRequest = null;
            isBatchExportingPng = false;
            isCancellingBatchExportPng = false;
            RaiseBatchCommands();
        }
    }

    private static string ReserveClassifiedPngName(string sourceName, ISet<string> reserved)
    {
        var suggested = PreviewPngFilename.Suggest(sourceName);
        if (reserved.Add(suggested)) return suggested;
        var stem = Path.GetFileNameWithoutExtension(suggested);
        for (var number = 1; ; number++)
        {
            var candidate = $"{stem} ({number}).png";
            if (reserved.Add(candidate)) return candidate;
        }
    }

    private async Task SaveClassifiedPreviewPngAsync(ClassifiedImage image, byte[] pngBytes)
    {
        var target = previewPngSaveDialog.PickSaveTarget(PreviewPngFilename.Suggest(image.Name));
        if (target is null) return;
        try
        {
            await previewPngWriter.WriteAsync(target.FullPath, pngBytes, target.AllowOverwrite, CancellationToken.None).ConfigureAwait(true);
            previewSaveStatusText = $"预览 PNG 已保存：{Path.GetFileName(target.FullPath)}";
        }
        catch (UnauthorizedAccessException) { previewSaveStatusText = "没有权限写入所选位置。未修改源归档。"; }
        catch (ArgumentException) { previewSaveStatusText = "所选保存位置无效。未写入文件。"; }
        catch (IOException) { previewSaveStatusText = "保存 PNG 时发生 I/O 错误。未完成文件已清理。"; }
        catch (Exception) { previewSaveStatusText = "保存 PNG 时发生内部错误。未完成文件已清理。"; }
        OnPropertyChanged(nameof(PreviewSaveStatusText));
        PublishMainExportStatus(previewSaveStatusText);
    }

    private async Task ExtractClassifiedImageAsync(ClassifiedImage image)
    {
        if (outputFolderPicker is null) return;
        var parent = outputFolderPicker.PickParentOutputDirectory(recentOutputParentDirectory);
        if (parent is null) return;
        try
        {
            using var session = archiveOpener.Open(image.ArchivePath);
            var plan = session.PlanExtraction([image.ArchiveEntryIndex], parent, availableSpaceProvider: availableSpaceProvider is null ? null : GetAvailableBytesSafely);
            var request = new ExtractionConfirmationRequest("当前图片原样资源", 1, plan.OutputRoot, plan.TotalBytes, plan.SpaceStatus, false);
            if (!confirmationService.Confirm(request)) return;
            var summary = await session.ExtractAsync(plan, extractionDecisionProvider, CancellationToken.None).ConfigureAwait(true);
            ExtractionSummaryText = $"完成：{summary.Results.Count(result => result.Status == "Written")} 写入，{summary.Results.Count(result => result.Status == "Renamed")} 重命名，{summary.Results.Count(result => result.Status == "Skipped")} 跳过，{summary.Results.Count(result => result.Status == "Failed")} 失败。\n输出目录：{plan.OutputRoot}";
        }
        catch (Exception)
        {
            ExtractionSummaryText = "原样资源导出失败：内部错误。源归档未被修改。";
        }
        OnPropertyChanged(nameof(ExtractionSummaryText));
        PublishMainExportStatus(ExtractionSummaryText);
    }

    private void SetClassifiedExportContext(ClassifiedImage image, IReadOnlyList<byte> pngBytes)
    {
        if (!Equals(selectedClassifiedImage, image)) return;
        classifiedPreviewPngBytes = pngBytes.ToArray();
        OnPropertyChanged(nameof(ExportSelectionText));
        OnPropertyChanged(nameof(CanExport));
        ((AsyncCommand)ExportCommand).RaiseCanExecuteChanged();
    }

    private void ClearClassifiedExportContext()
    {
        classifiedPreviewPngBytes = null;
        OnPropertyChanged(nameof(ExportSelectionText));
        OnPropertyChanged(nameof(CanExport));
        ((AsyncCommand)ExportCommand).RaiseCanExecuteChanged();
    }

    private async Task ExportAsync()
    {
        if (!CanExport) return;
        var selectedAudio = IsAudioMode ? FreezeSelectedClassifiedAudio() : Array.Empty<ClassifiedAudio>();
        var mediaKind = IsVideoMode ? ExportMediaKind.Video : IsAudioMode && selectedAudio.Count > 0 ? ExportMediaKind.Audio : ExportMediaKind.Image;
        var capability = PendingExportCapability ?? ExportCapabilityChooser.Choose(AppOwnerProvider.ResolveMainWindow(), mediaKind);
        PendingExportCapability = null;
        if (capability == ExportCapability.Cancel) return;
        if (IsAudioMode && selectedAudio.Count > 0)
        {
            if (capability == ExportCapability.RawResource)
            {
                if (selectedAudio.Count == 1) await SaveSingleRawAudioAsync(selectedAudio[0]).ConfigureAwait(true);
                else await ExtractClassifiedAudioBatchAsync(selectedAudio).ConfigureAwait(true);
            }
            else if (selectedAudio.Count == 1)
            {
                await ExportSingleCompatibleAudioAsync(selectedAudio[0]).ConfigureAwait(true);
            }
            else
            {
                await ExportCompatibleAudioBatchAsync(selectedAudio).ConfigureAwait(true);
            }
            return;
        }
        var selectedVideo = IsVideoMode ? FreezeSelectedClassifiedVideo() : Array.Empty<ClassifiedVideo>();
        if (IsVideoMode && selectedVideo.Count > 0)
        {
            if (capability == ExportCapability.RawResource)
            {
                if (selectedVideo.Count == 1) await SaveSingleRawVideoAsync(selectedVideo[0]).ConfigureAwait(true);
                else await ExtractClassifiedVideoBatchAsync(selectedVideo).ConfigureAwait(true);
            }
            else
            {
                if (selectedVideo.Count == 1) await ExportSingleCompatibleVideoAsync(selectedVideo[0]).ConfigureAwait(true);
                else await ExportCompatibleVideoBatchAsync(selectedVideo).ConfigureAwait(true);
            }
            return;
        }
        if (selectedClassifiedImage is not null && classifiedPreviewPngBytes is not null)
        {
            if (capability == ExportCapability.RawResource)
            {
                foreach (var image in selectedClassifiedImages)
                {
                    await ExtractClassifiedImageAsync(image).ConfigureAwait(true);
                }
                return;
            }

            if (selectedClassifiedImages.Count == 1)
            {
                var selected = selectedClassifiedImages[0];
                if (Equals(selected, selectedClassifiedImage) && classifiedPreviewPngBytes is not null)
                {
                    await SaveClassifiedPreviewPngAsync(selected, classifiedPreviewPngBytes).ConfigureAwait(true);
                }
                else
                {
                    await ExportClassifiedImagesPngAsync([selected]).ConfigureAwait(true);
                }
            }
            else
            {
                await ExportClassifiedImagesPngAsync(selectedClassifiedImages).ConfigureAwait(true);
            }
            return;
        }
        if (capability == ExportCapability.RawResource)
        {
            await ExtractAsync(false).ConfigureAwait(true);
            return;
        }
        if (SelectedCount == 1)
        {
            await SavePreviewPngAsync().ConfigureAwait(true);
            return;
        }
        await BatchExportSelectedPngAsync().ConfigureAwait(true);
    }

    private async Task ExportSingleCompatibleAudioAsync(ClassifiedAudio audio)
    {
        if (compatibleAudioSaveDialog is null) return;
        var target = compatibleAudioSaveDialog.PickSaveTarget(Path.ChangeExtension(Path.GetFileName(audio.Name), ".ogg"));
        if (target is null) return;
        var request = CreateClassifiedExportRequest();
        activeClassifiedExportRequest = request;
        isBatchExportingPng = true;
        isCancellingBatchExportPng = false;
        RaiseBatchCommands();
        try
        {
            using var session = archiveOpener.Open(audio.ArchivePath);
            await compatibleAudioWriter.WriteOggAsync(
                target.FullPath,
                (destination, token) => session.CopyHwOggPayloadToAsync(audio.ArchiveEntryIndex, destination, token),
                target.AllowOverwrite,
                request.Cancellation.Token).ConfigureAwait(true);
            if (CanPublishClassifiedExport(request))
                PublishMainExportStatus($"兼容 Ogg 导出完成：1 项成功，0 项失败。\n输出位置：{target.FullPath}");
        }
        catch (Exception error) when (IsCancelled(error, request.Cancellation))
        {
            if (CanPublishClassifiedExport(request))
                PublishMainExportStatus("兼容 Ogg 导出已取消：已提交文件保留，当前临时文件已清理。");
        }
        catch (Exception)
        {
            if (CanPublishClassifiedExport(request))
                PublishMainExportStatus("兼容 Ogg 导出失败：0 项成功，1 项失败。源归档未被修改。\n输出位置：" + target.FullPath);
        }
        finally
        {
            request.Completion.TrySetResult();
            request.Cancellation.Dispose();
            if (ReferenceEquals(activeClassifiedExportRequest, request)) activeClassifiedExportRequest = null;
            isBatchExportingPng = false;
            isCancellingBatchExportPng = false;
            RaiseBatchCommands();
        }
    }

    private async Task ExportSingleCompatibleVideoAsync(ClassifiedVideo video)
    {
        var target = compatibleVideoSaveDialog.PickSaveTarget(Path.ChangeExtension(Path.GetFileName(video.Name), ".mp4"));
        if (target is null) return;
        var request = CreateClassifiedExportRequest();
        activeClassifiedExportRequest = request;
        isBatchExportingPng = true;
        isCancellingBatchExportPng = false;
        RaiseBatchCommands();
        try
        {
            using var session = archiveOpener.Open(video.ArchivePath);
            await compatibleVideoWriter.WriteMp4Async(target.FullPath, (destination, token) => session.CopySupportedMp4ToAsync(video.ArchiveEntryIndex, destination, token), target.AllowOverwrite, request.Cancellation.Token).ConfigureAwait(true);
            if (CanPublishClassifiedExport(request))
                PublishMainExportStatus($"兼容 MP4 导出完成：1 项成功，0 项失败。\n输出位置：{target.FullPath}");
        }
        catch (Exception error) when (IsCancelled(error, request.Cancellation))
        {
            if (CanPublishClassifiedExport(request))
                PublishMainExportStatus("兼容 MP4 导出已取消：已提交文件保留，当前临时文件已清理。");
        }
        catch (Exception)
        {
            if (CanPublishClassifiedExport(request))
                PublishMainExportStatus("兼容 MP4 导出失败：0 项成功，1 项失败。源归档未被修改。\n输出位置：" + target.FullPath);
        }
        finally
        {
            request.Completion.TrySetResult();
            request.Cancellation.Dispose();
            if (ReferenceEquals(activeClassifiedExportRequest, request)) activeClassifiedExportRequest = null;
            isBatchExportingPng = false;
            isCancellingBatchExportPng = false;
            RaiseBatchCommands();
        }
    }

    private async Task SaveSingleRawVideoAsync(ClassifiedVideo video)
    {
        var target = rawResourceSaveDialog.PickSaveTarget(Path.GetFileName(video.Name));
        if (target is null) return;
        var request = CreateClassifiedExportRequest();
        activeClassifiedExportRequest = request;
        isBatchExportingPng = true;
        isCancellingBatchExportPng = false;
        RaiseBatchCommands();
        try
        {
            using var session = archiveOpener.Open(video.ArchivePath);
            await rawResourceWriter.WriteAsync(target.FullPath, (destination, token) => session.CopyEntryToAsync(video.ArchiveEntryIndex, destination, token), target.AllowOverwrite, request.Cancellation.Token).ConfigureAwait(true);
            if (CanPublishClassifiedExport(request))
                PublishMainExportStatus($"原样资源导出完成：1 项成功，0 项失败。\n输出位置：{target.FullPath}");
        }
        catch (Exception error) when (IsCancelled(error, request.Cancellation))
        {
            if (CanPublishClassifiedExport(request))
                PublishMainExportStatus("原样资源导出已取消：已提交文件保留，当前临时文件已清理。");
        }
        catch (Exception)
        {
            if (CanPublishClassifiedExport(request))
                PublishMainExportStatus("原样资源导出失败：0 项成功，1 项失败。源归档未被修改。\n输出位置：" + target.FullPath);
        }
        finally
        {
            request.Completion.TrySetResult();
            request.Cancellation.Dispose();
            if (ReferenceEquals(activeClassifiedExportRequest, request)) activeClassifiedExportRequest = null;
            isBatchExportingPng = false;
            isCancellingBatchExportPng = false;
            RaiseBatchCommands();
        }
    }

    private async Task ExportCompatibleVideoBatchAsync(IReadOnlyList<ClassifiedVideo> videos)
    {
        var outputFolder = batchPngOutputFolderPicker.PickOutputFolder(recentOutputParentDirectory);
        if (outputFolder is null) return;
        Directory.CreateDirectory(outputFolder);
        var reserved = Directory.EnumerateFileSystemEntries(outputFolder).Select(Path.GetFileName).Where(name => name is not null).Cast<string>().ToHashSet(StringComparer.OrdinalIgnoreCase);
        var conflicts = videos.Count(video => reserved.Contains(Path.ChangeExtension(Path.GetFileName(video.Name), ".mp4")));
        var decision = conflicts == 0 ? BatchPngConflictDecision.RenameAll : batchPngConflictConfirmationService.Decide(new BatchPngConflictRequest(conflicts));
        if (decision == BatchPngConflictDecision.Cancel)
        {
            PublishMainExportStatus($"兼容 MP4 导出已取消：0 项成功，0 项失败，0 项取消，0 项冲突跳过，{videos.Count} 项未开始。未写入文件。");
            return;
        }
        var request = CreateClassifiedExportRequest();
        activeClassifiedExportRequest = request;
        isBatchExportingPng = true;
        isCancellingBatchExportPng = false;
        RaiseBatchCommands();
        var written = 0;
        var renamed = 0;
        var failed = 0;
        var conflictSkipped = 0;
        var cancelled = 0;
        var notStarted = 0;
        try
        {
            for (var index = 0; index < videos.Count; index++)
            {
                var video = videos[index];
                if (request.Cancellation.IsCancellationRequested) { notStarted = videos.Count - index; break; }
                var suggested = Path.ChangeExtension(Path.GetFileName(video.Name), ".mp4");
                if (reserved.Contains(suggested) && decision == BatchPngConflictDecision.SkipAll) { conflictSkipped++; continue; }
                var targetName = ReserveCompatibleVideoName(suggested, reserved);
                try
                {
                    using var session = archiveOpener.Open(video.ArchivePath);
                    await compatibleVideoWriter.WriteMp4Async(Path.Combine(outputFolder, targetName), (destination, token) => session.CopySupportedMp4ToAsync(video.ArchiveEntryIndex, destination, token), false, request.Cancellation.Token).ConfigureAwait(true);
                    if (string.Equals(targetName, suggested, StringComparison.OrdinalIgnoreCase)) written++; else renamed++;
                }
                catch (Exception error) when (IsCancelled(error, request.Cancellation)) { cancelled++; notStarted = videos.Count - index - 1; break; }
                catch (Exception) { failed++; }
            }
            if (CanPublishClassifiedExport(request))
                PublishMainExportStatus($"兼容 MP4 导出完成：{written} 项写入，{renamed} 项重命名，{conflictSkipped} 项冲突跳过，{failed} 项失败，{cancelled} 项取消，{notStarted} 项未开始。\n输出目录：{outputFolder}");
        }
        finally
        {
            request.Completion.TrySetResult();
            request.Cancellation.Dispose();
            if (ReferenceEquals(activeClassifiedExportRequest, request)) activeClassifiedExportRequest = null;
            isBatchExportingPng = false;
            isCancellingBatchExportPng = false;
            RaiseBatchCommands();
        }
    }

    private static string ReserveCompatibleVideoName(string suggested, ISet<string> reserved)
    {
        if (reserved.Add(suggested)) return suggested;
        var stem = Path.GetFileNameWithoutExtension(suggested);
        for (var number = 1; ; number++)
        {
            var candidate = $"{stem} ({number}).mp4";
            if (reserved.Add(candidate)) return candidate;
        }
    }

    private async Task ExtractClassifiedVideoBatchAsync(IReadOnlyList<ClassifiedVideo> videos)
    {
        if (outputFolderPicker is null) return;
        var parent = outputFolderPicker.PickParentOutputDirectory(recentOutputParentDirectory);
        if (parent is null) return;
        var request = CreateClassifiedExportRequest();
        activeClassifiedExportRequest = request;
        isBatchExportingPng = true;
        isCancellingBatchExportPng = false;
        RaiseBatchCommands();
        var results = new List<ApplicationExtractionResult>();
        try
        {
            var groups = videos.GroupBy(video => video.ArchivePath, StringComparer.OrdinalIgnoreCase).ToArray();
            for (var groupIndex = 0; groupIndex < groups.Length; groupIndex++)
            {
                var group = groups[groupIndex];
                if (request.Cancellation.IsCancellationRequested)
                {
                    AddRawVideoTerminalResults(results, groups.Skip(groupIndex).SelectMany(items => items), "NotStarted", null);
                    break;
                }
                try
                {
                    using var session = archiveOpener.Open(group.Key);
                    var indices = group.Select(video => video.ArchiveEntryIndex).ToArray();
                    var plan = session.PlanExtraction(indices, parent, availableSpaceProvider: availableSpaceProvider is null ? null : GetAvailableBytesSafely);
                    if (!confirmationService.Confirm(new ExtractionConfirmationRequest("选中视频原样资源", indices.Length, plan.OutputRoot, plan.TotalBytes, plan.SpaceStatus, false)))
                    {
                        AddRawVideoTerminalResults(results, groups.Skip(groupIndex).SelectMany(items => items), "NotStarted", null);
                        break;
                    }
                    var summary = await session.ExtractAsync(plan, extractionDecisionProvider, request.Cancellation.Token).ConfigureAwait(true);
                    results.AddRange(summary.Results);
                    if (summary.Cancelled || request.Cancellation.IsCancellationRequested)
                    {
                        AddRawVideoTerminalResults(results, groups.Skip(groupIndex + 1).SelectMany(items => items), "NotStarted", null);
                        break;
                    }
                }
                catch (Exception error) when (IsCancelled(error, request.Cancellation))
                {
                    AddRawVideoTerminalResults(results, group, "Cancelled", "Cancelled");
                    AddRawVideoTerminalResults(results, groups.Skip(groupIndex + 1).SelectMany(items => items), "NotStarted", null);
                    break;
                }
                catch (Exception) { AddRawVideoTerminalResults(results, group, "Failed", "InternalError"); }
            }
            if (CanPublishClassifiedExport(request) && !request.Cancellation.IsCancellationRequested)
            {
                var written = results.Count(result => result.Status == "Written");
                var renamed = results.Count(result => result.Status == "Renamed");
                var conflictSkipped = results.Count(result => result.Status == "Skipped");
                var failed = results.Count(result => result.Status == "Failed");
                var cancelled = results.Count(result => result.Status == "Cancelled");
                var notStarted = results.Count(result => result.Status == "NotStarted");
                PublishMainExportStatus($"原样资源导出完成：{written} 项写入，{renamed} 项重命名，{conflictSkipped} 项冲突跳过，{failed} 项失败，{cancelled} 项取消，{notStarted} 项未开始。\n输出目录：{parent}");
            }
        }
        finally
        {
            request.Completion.TrySetResult();
            request.Cancellation.Dispose();
            if (ReferenceEquals(activeClassifiedExportRequest, request)) activeClassifiedExportRequest = null;
            isBatchExportingPng = false;
            isCancellingBatchExportPng = false;
            RaiseBatchCommands();
        }
    }

    private static void AddRawVideoTerminalResults(ICollection<ApplicationExtractionResult> results, IEnumerable<ClassifiedVideo> videos, string status, string? errorCategory)
    {
        foreach (var video in videos) results.Add(new ApplicationExtractionResult(video.ArchiveEntryIndex, video.Name, null, status, 0, errorCategory));
    }

    private async Task ExportCompatibleAudioBatchAsync(IReadOnlyList<ClassifiedAudio> audio)
    {
        var outputFolder = batchPngOutputFolderPicker.PickOutputFolder(recentOutputParentDirectory);
        if (outputFolder is null) return;
        Directory.CreateDirectory(outputFolder);
        var existing = Directory.EnumerateFileSystemEntries(outputFolder)
            .Select(Path.GetFileName)
            .Where(name => name is not null)
            .Cast<string>()
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var conflicts = audio.Count(item => existing.Contains(Path.ChangeExtension(Path.GetFileName(item.Name), ".ogg")));
        var decision = conflicts == 0
            ? BatchPngConflictDecision.RenameAll
            : batchPngConflictConfirmationService.Decide(new BatchPngConflictRequest(conflicts));
        if (decision == BatchPngConflictDecision.Cancel) return;

        var request = CreateClassifiedExportRequest();
        activeClassifiedExportRequest = request;
        isBatchExportingPng = true;
        isCancellingBatchExportPng = false;
        RaiseBatchCommands();
        var written = 0;
        var failed = 0;
        var cancelled = 0;
        var conflictSkipped = 0;
        try
        {
            foreach (var item in audio)
            {
                if (request.Cancellation.IsCancellationRequested)
                {
                    cancelled = audio.Count - written - failed - conflictSkipped;
                    break;
                }
                var suggested = Path.ChangeExtension(Path.GetFileName(item.Name), ".ogg");
                if (existing.Contains(suggested) && decision == BatchPngConflictDecision.SkipAll)
                {
                    conflictSkipped++;
                    continue;
                }
                var targetName = ReserveCompatibleAudioName(suggested, existing);
                try
                {
                    using var session = archiveOpener.Open(item.ArchivePath);
                    await compatibleAudioWriter.WriteOggAsync(
                        Path.Combine(outputFolder, targetName),
                        (destination, token) => session.CopyHwOggPayloadToAsync(item.ArchiveEntryIndex, destination, token),
                        false,
                        request.Cancellation.Token).ConfigureAwait(true);
                    written++;
                }
                catch (Exception error) when (IsCancelled(error, request.Cancellation))
                {
                    cancelled = audio.Count - written - failed - conflictSkipped;
                    break;
                }
                catch (Exception) { failed++; }
            }
            if (CanPublishClassifiedExport(request))
            {
                PublishMainExportStatus($"兼容 Ogg 导出完成：{written} 项成功，{failed} 项失败，{cancelled} 项取消，{conflictSkipped} 项冲突跳过。\n输出目录：{outputFolder}");
            }
        }
        finally
        {
            request.Completion.TrySetResult();
            request.Cancellation.Dispose();
            if (ReferenceEquals(activeClassifiedExportRequest, request)) activeClassifiedExportRequest = null;
            isBatchExportingPng = false;
            isCancellingBatchExportPng = false;
            RaiseBatchCommands();
        }
    }

    private async Task SaveSingleRawAudioAsync(ClassifiedAudio audio)
    {
        var target = rawResourceSaveDialog.PickSaveTarget(Path.GetFileName(audio.Name));
        if (target is null) return;
        var request = CreateClassifiedExportRequest();
        activeClassifiedExportRequest = request;
        isBatchExportingPng = true;
        isCancellingBatchExportPng = false;
        RaiseBatchCommands();
        try
        {
            using var session = archiveOpener.Open(audio.ArchivePath);
            await rawResourceWriter.WriteAsync(
                target.FullPath,
                (destination, token) => session.CopyEntryToAsync(audio.ArchiveEntryIndex, destination, token),
                target.AllowOverwrite,
                request.Cancellation.Token).ConfigureAwait(true);
            if (CanPublishClassifiedExport(request))
                PublishMainExportStatus($"原样资源导出完成：1 项成功，0 项失败。\n输出位置：{target.FullPath}");
        }
        catch (Exception error) when (IsCancelled(error, request.Cancellation))
        {
            if (CanPublishClassifiedExport(request))
                PublishMainExportStatus("原样资源导出已取消：已提交文件保留，当前临时文件已清理。");
        }
        catch (Exception)
        {
            if (CanPublishClassifiedExport(request))
                PublishMainExportStatus("原样资源导出失败：0 项成功，1 项失败。源归档未被修改。\n输出位置：" + target.FullPath);
        }
        finally
        {
            request.Completion.TrySetResult();
            request.Cancellation.Dispose();
            if (ReferenceEquals(activeClassifiedExportRequest, request)) activeClassifiedExportRequest = null;
            isBatchExportingPng = false;
            isCancellingBatchExportPng = false;
            RaiseBatchCommands();
        }
    }

    private static string ReserveCompatibleAudioName(string suggested, ISet<string> reserved)
    {
        if (reserved.Add(suggested)) return suggested;
        var stem = Path.GetFileNameWithoutExtension(suggested);
        for (var number = 1; ; number++)
        {
            var candidate = $"{stem} ({number}).ogg";
            if (reserved.Add(candidate)) return candidate;
        }
    }

    private async Task ExtractClassifiedAudioAsync(ClassifiedAudio audio)
    {
        await ExtractClassifiedAudioBatchAsync([audio]).ConfigureAwait(true);
    }

    private async Task ExtractClassifiedAudioBatchAsync(IReadOnlyList<ClassifiedAudio> audio)
    {
        if (outputFolderPicker is null || audio.Count == 0) return;
        var parent = outputFolderPicker.PickParentOutputDirectory(recentOutputParentDirectory);
        if (parent is null) return;
        var request = CreateClassifiedExportRequest();
        activeClassifiedExportRequest = request;
        isBatchExportingPng = true;
        isCancellingBatchExportPng = false;
        OnPropertyChanged(nameof(IsBatchExportingPng));
        OnPropertyChanged(nameof(IsCancellingBatchExportPng));
        RaiseBatchCommands();
        try
        {
            var results = new List<ApplicationExtractionResult>();
            var groups = audio.GroupBy(item => item.ArchivePath, StringComparer.OrdinalIgnoreCase).ToArray();
            for (var groupIndex = 0; groupIndex < groups.Length; groupIndex++)
            {
                var group = groups[groupIndex];
                if (request.Cancellation.IsCancellationRequested)
                {
                    AddRawAudioTerminalResults(results, groups.Skip(groupIndex).SelectMany(items => items), "NotStarted", null);
                    break;
                }
                try
                {
                    using var session = archiveOpener.Open(group.Key);
                    var indices = group.Select(item => item.ArchiveEntryIndex).ToArray();
                    var plan = session.PlanExtraction(indices, parent, availableSpaceProvider: availableSpaceProvider is null ? null : GetAvailableBytesSafely);
                    var confirmationRequest = new ExtractionConfirmationRequest("选中音频原样资源", indices.Length, plan.OutputRoot, plan.TotalBytes, plan.SpaceStatus, false);
                    if (!confirmationService.Confirm(confirmationRequest))
                    {
                        AddRawAudioTerminalResults(results, groups.Skip(groupIndex).SelectMany(items => items), "NotStarted", null);
                        break;
                    }
                    var summary = await session.ExtractAsync(plan, extractionDecisionProvider, request.Cancellation.Token).ConfigureAwait(true);
                    results.AddRange(summary.Results);
                    if (summary.Cancelled || request.Cancellation.IsCancellationRequested)
                    {
                        AddRawAudioTerminalResults(results, groups.Skip(groupIndex + 1).SelectMany(items => items), "NotStarted", null);
                        break;
                    }
                }
                catch (Exception error) when (IsCancelled(error, request.Cancellation))
                {
                    AddRawAudioTerminalResults(results, group, "Cancelled", "Cancelled");
                    AddRawAudioTerminalResults(results, groups.Skip(groupIndex + 1).SelectMany(items => items), "NotStarted", null);
                    break;
                }
                catch (Exception)
                {
                    AddRawAudioTerminalResults(results, group, "Failed", "InternalError");
                }
            }
            if (CanPublishClassifiedExport(request))
            {
                PublishMainExportStatus($"原样资源导出完成：{results.Count(result => result.Status is "Written" or "Renamed")} 项成功，{results.Count(result => result.Status == "Failed")} 项失败，{results.Count(result => result.Status == "Cancelled")} 项取消，{results.Count(result => result.Status == "NotStarted")} 项未开始。\n输出目录：{parent}");
            }
        }
        finally
        {
            request.Completion.TrySetResult();
            request.Cancellation.Dispose();
            if (ReferenceEquals(activeClassifiedExportRequest, request)) activeClassifiedExportRequest = null;
            isBatchExportingPng = false;
            isCancellingBatchExportPng = false;
            OnPropertyChanged(nameof(IsBatchExportingPng));
            OnPropertyChanged(nameof(IsCancellingBatchExportPng));
            RaiseBatchCommands();
        }
    }

    private static void AddRawAudioTerminalResults(ICollection<ApplicationExtractionResult> results, IEnumerable<ClassifiedAudio> audio, string status, string? errorCategory)
    {
        foreach (var item in audio)
        {
            results.Add(new ApplicationExtractionResult(item.ArchiveEntryIndex, item.Name, null, status, 0, errorCategory));
        }
    }

    private async Task BatchExportSelectedPngAsync()
    {
        if (!CanBatchExportSelectedPng || previewSession is not IArchivePreviewSession session) return;
        var selection = BatchPngSelectionSnapshot.Capture(Entries, selectedArchiveIndices);
        var outputFolder = batchPngOutputFolderPicker.PickOutputFolder(recentOutputParentDirectory);
        if (outputFolder is null) return;
        Directory.CreateDirectory(outputFolder);
        var existing = Directory.EnumerateFileSystemEntries(outputFolder).Select(Path.GetFileName).Where(name => name is not null).Cast<string>().ToArray();
        var probe = BatchPngExportPlan.Create(selection.Entries, outputFolder, existing, BatchPngConflictDecision.RenameAll);
        var decision = probe.ConflictCount == 0 ? BatchPngConflictDecision.RenameAll : batchPngConflictConfirmationService.Decide(new BatchPngConflictRequest(probe.ConflictCount));
        var plan = BatchPngExportPlan.Create(selection.Entries, outputFolder, existing, decision);
        var request = new BatchPngRequest(selection, session, plan, new CancellationTokenSource());
        activeBatchPngRequest = request;
        isBatchExportingPng = true;
        isCancellingBatchExportPng = false;
        batchExportSummaryText = string.Empty;
        OnPropertyChanged(nameof(IsBatchExportingPng));
        OnPropertyChanged(nameof(IsCancellingBatchExportPng));
        OnPropertyChanged(nameof(BatchExportSummaryText));
        RaiseBatchCommands();
        try
        {
            var progress = new Progress<BatchPngExportProgress>(value =>
            {
                if (ReferenceEquals(activeBatchPngRequest, request) && !disposed)
                {
                    batchExportProgressText = $"正在导出 {value.Position}/{value.Total}：{value.Name}";
                    OnPropertyChanged(nameof(BatchExportProgressText));
                }
            });
            var summary = await batchPngExportExecutor.ExecuteAsync(selection, session, plan, previewPngWriter, request.Cancellation.Token, progress, previewSessionGate);
            if (ReferenceEquals(activeBatchPngRequest, request) && !disposed)
            {
                batchExportSummaryText = $"完成：{summary.WrittenCount} 写入，{summary.NonImageSkippedCount} 非图片跳过，{summary.ConflictSkippedCount} 冲突跳过，{summary.FailedCount} 失败，{summary.CancelledCount} 取消，{summary.NotStartedCount} 未开始。";
                OnPropertyChanged(nameof(BatchExportSummaryText));
            }
        }
        finally
        {
            request.Completion.TrySetResult();
            request.Cancellation.Dispose();
            if (ReferenceEquals(activeBatchPngRequest, request))
            {
                activeBatchPngRequest = null;
                isBatchExportingPng = false;
                isCancellingBatchExportPng = false;
                OnPropertyChanged(nameof(IsBatchExportingPng));
                OnPropertyChanged(nameof(IsCancellingBatchExportPng));
                RaiseBatchCommands();
            }
        }
    }

    private void CancelBatchExportPng()
    {
        if (activeClassifiedExportRequest is { } classifiedRequest && !isCancellingBatchExportPng)
        {
            isCancellingBatchExportPng = true;
            classifiedRequest.Cancellation.Cancel();
            OnPropertyChanged(nameof(IsCancellingBatchExportPng));
            RaiseBatchCommands();
            return;
        }
        if (activeBatchPngRequest is null || isCancellingBatchExportPng) return;
        isCancellingBatchExportPng = true;
        activeBatchPngRequest.Cancellation.Cancel();
        OnPropertyChanged(nameof(IsCancellingBatchExportPng));
        RaiseBatchCommands();
    }

    private async Task CancelAndWaitClassifiedExportAsync()
    {
        var request = activeClassifiedExportRequest;
        if (request is null) return;
        request.SuppressPublication = true;
        request.Cancellation.Cancel();
        await request.Completion.Task.ConfigureAwait(true);
    }

    private ClassifiedExportRequest CreateClassifiedExportRequest() => new(
        gameRootPath,
        CurrentExportCategory,
        new CancellationTokenSource());

    private ClassifiedExportCategory CurrentExportCategory => IsVideoMode
        ? ClassifiedExportCategory.Video
        : IsAudioMode ? ClassifiedExportCategory.Audio : ClassifiedExportCategory.Image;

    private bool CanPublishClassifiedExport(ClassifiedExportRequest request) =>
        ReferenceEquals(activeClassifiedExportRequest, request) &&
        !disposed &&
        !request.SuppressPublication &&
        string.Equals(gameRootPath, request.Root, StringComparison.OrdinalIgnoreCase) &&
        CurrentExportCategory == request.Category;

    private void RaiseBatchCommands()
    {
        ((AsyncCommand)OpenCommand).RaiseCanExecuteChanged();
        ((SyncCommand)SelectAllCommand).RaiseCanExecuteChanged();
        ((AsyncCommand)PreviewCommand).RaiseCanExecuteChanged();
        ((AsyncCommand)CancelPreviewCommand).RaiseCanExecuteChanged();
        ((AsyncCommand)SavePreviewPngCommand).RaiseCanExecuteChanged();
        ((AsyncCommand)ExtractSelectedCommand).RaiseCanExecuteChanged();
        ((AsyncCommand)ExtractAllCommand).RaiseCanExecuteChanged();
        ((AsyncCommand)BatchExportSelectedPngCommand).RaiseCanExecuteChanged();
        ((SyncCommand)CancelBatchExportPngCommand).RaiseCanExecuteChanged();
        ((AsyncCommand)ExportCommand).RaiseCanExecuteChanged();
        OnPropertyChanged(nameof(CanBatchExportSelectedPng));
        OnPropertyChanged(nameof(CanExport));
        OnPropertyChanged(nameof(IsEntryListInteractive));
    }

    private bool HasValidPreviewSaveSnapshot =>
        previewSaveSnapshot is not null &&
        previewSession is not null &&
        selectedArchiveIndices.Count == 1 &&
        selectedArchiveIndices.Contains(previewSaveSnapshot.Entry.Index) &&
        ReferenceEquals(previewSaveSnapshot.Session, previewSession) &&
        Equals(previewSaveSnapshot.Entry, SelectedEntry) &&
        PreviewImage is not null;

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        gameRootScanCancellation?.Cancel();
        gameRootScanCancellation?.Dispose();
        gameRootScanCompletion.TrySetResult();
        imageClassificationCancellation?.Cancel();
        activeClassifiedExportRequest?.Cancellation.Cancel();
        lock (activeClassifiedPreviewRequests)
        {
            foreach (var request in activeClassifiedPreviewRequests) request.Cancellation.Cancel();
        }
        TransparencyHintText = string.Empty;
        operationVersion++;
        previewVersion++;
        openCancellation?.Cancel();
        openCancellation?.Dispose();
        currentPreviewRequest?.Cancellation.Cancel();
        foreach (var request in activePreviewRequests.ToArray()) request.Cancellation.Cancel();
        extractionCancellation?.Cancel();
        if (!IsExtracting && activePreviewRequests.Count == 0)
        {
            extractionCancellation?.Dispose();
            extractionCancellation = null;
            previewSession?.Dispose();
            previewSession = null;
        }
    }

    private IReadOnlyList<PreviewRequest> InvalidatePreview()
    {
        var invalidatedRequests = activePreviewRequests.ToArray();
        foreach (var request in invalidatedRequests)
        {
            request.Cancellation.Cancel();
        }

        previewVersion++;
        currentPreviewRequest = null;
        selectedEntry = null;
        PreviewImage = null;
        TransparencyHintText = string.Empty;
        previewSaveSnapshot = null;
        IsUnsupportedPreviewSelection = false;
        isPreviewLoading = false;
        OnPropertyChanged(nameof(SelectedEntry));
        OnPropertyChanged(nameof(IsPreviewLoading));
        PreviewStatusText = "请选择一个 MZP 条目。";
        ((AsyncCommand)PreviewCommand).RaiseCanExecuteChanged();
        ((AsyncCommand)CancelPreviewCommand).RaiseCanExecuteChanged();
        ((AsyncCommand)SavePreviewPngCommand).RaiseCanExecuteChanged();
        OnPropertyChanged(nameof(CanSavePreviewPng));
        return invalidatedRequests;
    }

    private sealed class ClassifiedPreviewRequest(ClassifiedImage image, CancellationTokenSource cancellation)
    {
        public ClassifiedImage Image { get; } = image;
        public CancellationTokenSource Cancellation { get; } = cancellation;
        public TaskCompletionSource Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool GateEntered { get; set; }
    }

    private sealed class PreviewRequest
    {
        public PreviewRequest(long version, IArchiveSession session, ArchiveEntryProjection entry, CancellationTokenSource cancellation)
        {
            Version = version;
            Session = session;
            Entry = entry;
            Cancellation = cancellation;
        }

        public long Version { get; }
        public IArchiveSession Session { get; }
        public ArchiveEntryProjection Entry { get; }
        public CancellationTokenSource Cancellation { get; }
        public TaskCompletionSource Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public PreviewPhase Phase { get; set; }
    }

    private sealed record PreviewSaveSnapshot(IArchiveSession Session, ArchiveEntryProjection Entry, byte[] PngBytes);

    private sealed class SaveRequest(PreviewSaveSnapshot snapshot, PreviewPngSaveTarget target, CancellationTokenSource cancellation)
    {
        public PreviewSaveSnapshot Snapshot { get; } = snapshot;
        public PreviewPngSaveTarget Target { get; } = target;
        public CancellationTokenSource Cancellation { get; } = cancellation;
        public TaskCompletionSource Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class ClassifiedExportRequest(string? root, ClassifiedExportCategory category, CancellationTokenSource cancellation)
    {
        public string? Root { get; } = root;
        public ClassifiedExportCategory Category { get; } = category;
        public CancellationTokenSource Cancellation { get; } = cancellation;
        public TaskCompletionSource Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool SuppressPublication { get; set; }
    }

    private enum ClassifiedExportCategory { Image, Audio, Video }

    private sealed class AudioClassificationRequest(string root, CancellationTokenSource cancellation)
    {
        public string Root { get; } = root;
        public CancellationTokenSource Cancellation { get; } = cancellation;
        public TaskCompletionSource Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class VideoClassificationRequest(string root, CancellationTokenSource cancellation)
    {
        public string Root { get; } = root;
        public CancellationTokenSource Cancellation { get; } = cancellation;
        public TaskCompletionSource Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private static bool IsCancelled(Exception error, CancellationTokenSource cancellation) =>
        cancellation.IsCancellationRequested && (error is OperationCanceledException || error is VNAssetExplorer.Application.ApplicationException { Code: ApplicationErrorCode.Cancelled });

    private sealed class BatchPngRequest(BatchPngSelectionSnapshot selection, IArchivePreviewSession session, BatchPngExportPlan plan, CancellationTokenSource cancellation)
    {
        public BatchPngSelectionSnapshot Selection { get; } = selection;
        public IArchivePreviewSession Session { get; } = session;
        public BatchPngExportPlan Plan { get; } = plan;
        public CancellationTokenSource Cancellation { get; } = cancellation;
        public TaskCompletionSource Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private enum PreviewPhase
    {
        PendingDelay,
        RunningPreview,
    }

    private sealed record PreparedExtraction(IArchiveSession Session, IReadOnlyList<int> Indices, ExtractionPlan Plan, ExtractionConfirmationRequest Request);

    private static bool IsPreviewCandidate(ArchiveEntryProjection entry) =>
        entry.Name.EndsWith(".mzp", StringComparison.OrdinalIgnoreCase);

    private static string MapError(ApplicationErrorCode code) => code switch
    {
        ApplicationErrorCode.AccessDenied => "没有权限读取该 HFA 文件。源归档未被修改。",
        ApplicationErrorCode.IoFailure => "读取 HFA 时发生 I/O 错误。源归档未被修改。",
        ApplicationErrorCode.InvalidOrUnsafeFormat => "该文件不是可安全读取的 HFA，或结构已损坏。源归档未被修改。",
        _ => "打开文件时发生内部错误。源归档未被修改。",
    };

    private static string MapPreviewError(ApplicationErrorCode code) => code switch
    {
        ApplicationErrorCode.UnsupportedPreviewFormat => "当前条目不是受支持的 MZP 图片。源归档未被修改。",
        ApplicationErrorCode.InvalidOrUnsafeFormat => "当前 MZP 图片损坏或不安全。源归档未被修改。",
        ApplicationErrorCode.ResourceLimitExceeded => "当前图片超过安全资源限制。源归档未被修改。",
        ApplicationErrorCode.AccessDenied => "没有权限读取当前图片。源归档未被修改。",
        ApplicationErrorCode.IoFailure => "读取当前图片时发生 I/O 错误。源归档未被修改。",
        ApplicationErrorCode.Cancelled => "预览操作已取消。源归档未被修改。",
        _ => "生成预览时发生内部错误。源归档未被修改。",
    };

    private static System.Windows.Media.Imaging.BitmapImage CreateBitmap(IReadOnlyList<byte> pngBytes)
    {
        using var stream = new MemoryStream(pngBytes.ToArray(), writable: false);
        var image = new System.Windows.Media.Imaging.BitmapImage();
        image.BeginInit();
        image.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
        image.StreamSource = stream;
        image.EndInit();
        image.Freeze();
        return image;
    }

    private static bool HasTransparency(System.Windows.Media.Imaging.BitmapSource bitmap)
    {
        if (bitmap.Format == System.Windows.Media.PixelFormats.Bgr32 || bitmap.Format == System.Windows.Media.PixelFormats.Bgr24)
        {
            return false;
        }

        var converted = new System.Windows.Media.Imaging.FormatConvertedBitmap(bitmap, System.Windows.Media.PixelFormats.Bgra32, null, 0);
        var pixels = new byte[converted.PixelWidth * converted.PixelHeight * 4];
        converted.CopyPixels(pixels, converted.PixelWidth * 4, 0);
        for (var i = 3; i < pixels.Length; i += 4)
        {
            if (pixels[i] < byte.MaxValue) return true;
        }
        return false;
    }

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        OnPropertyChanged(propertyName);
        if (propertyName == nameof(State))
        {
            OnPropertyChanged(nameof(IsLoading));
        }
        return true;
    }

    private void PublishMainExportStatus(string text)
    {
        mainExportStatusText = text;
        OnPropertyChanged(nameof(MainExportStatusText));
    }

    public void SetMainExportStatusForTesting(string text) => PublishMainExportStatus(text);

    private void OnPropertyChanged(string? propertyName) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    private sealed class AsyncCommand(Func<Task> execute, Func<bool> canExecute) : ICommand
    {
        private bool executing;

        public event EventHandler? CanExecuteChanged;

        public bool CanExecute(object? parameter) => !executing && canExecute();

        public async void Execute(object? parameter)
        {
            if (!CanExecute(parameter))
            {
                return;
            }

            executing = true;
            RaiseCanExecuteChanged();
            try
            {
                await execute();
            }
            catch (Exception)
            {
            }
            finally
            {
                executing = false;
                RaiseCanExecuteChanged();
            }
        }

        public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
    }

    private sealed class SyncCommand(Action execute, Func<bool> canExecute) : ICommand
    {
        public event EventHandler? CanExecuteChanged;
        public bool CanExecute(object? parameter) => canExecute();
        public void Execute(object? parameter) => execute();
        public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
    }
}
