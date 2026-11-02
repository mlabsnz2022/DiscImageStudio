using System.IO;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DiscImageStudio.BluRay;
using DiscImageStudio.Cd;
using DiscImageStudio.Core;
using DiscImageStudio.Dvd;
using DiscImageStudio.Imaging;
using DiscImageStudio.Services;

namespace DiscImageStudio.ViewModels;

/// Live read-back preview pipeline: 450ms debounce, revision-based stale-result discard,
/// running/pending coalescing, cancel-on-change. Drawn on STA worker threads.
///
/// Two input modes share that pipeline:
/// - Calibration preview (default): the input is the source picture and two parameter sets
///   describe it — the generated geometry lays it out, the measured geometry reads it back.
///   Rendering never touches the multi-hundred-megabyte artifact, which is what makes parameter
///   iteration cheap; the price is that the picture is a projection of the source, not a
///   read-back of the bytes that were actually written.
/// - Read-back simulation: the input is the generated burn artifact (CD track or DVD ISO,
///   matching the cdimage calibration model), so the picture shows what the disc really
///   carries. One parameter set describes the measured disc geometry.
public partial class LivePreviewViewModel : ObservableObject
{
    private readonly DispatcherTimer _livePreviewTimer;
    private readonly DiscParametersState _state;
    private readonly ShellViewModel _shell;

    /// <summary>Exposed for the view's two-way bindings to the shared parameters.</summary>
    public DiscParametersState State => _state;

    /// <summary>The parameter panel. It is a lens over both disc families, so the family switch
    /// re-points it instead of swapping in a second copy of the panel.</summary>
    public PreviewInspectorViewModel Inspector { get; }

    /// <summary>Workspace navigation for the page header's "Next" action.</summary>
    public IRelayCommand<Workspace> NavigateCommand => _shell.NavigateCommand;

    /// <summary>Family chip in the page header.</summary>
    public string DiscFamilyLabel => SelectedFamily switch
    {
        OpticalDiscFamily.Dvd => "DVD",
        OpticalDiscFamily.BluRay => "BD",
        _ => "CD",
    };

    /// <summary>Opens the project settings dialog, which owns the generation geometry. The
    /// preview shows that geometry read-only and links here instead of editing it a second
    /// time.</summary>
    public IAsyncRelayCommand EditGeometryCommand => _shell.EditProjectCommand;

    private CancellationTokenSource? _livePreviewCancellation;
    private bool _livePreviewRefreshRunning;
    private bool _livePreviewRefreshPending;
    private bool _isLivePreviewReady;
    private bool _suppressDiscTypeAutoUpdate;
    private bool _suppressPreviewModeAutoUpdate;
    private int _livePreviewRevision;
    private string? _lastLivePreviewPath;
    // True only while the canvas shows a picture the export action can write out: the
    // read-back simulation or the calibration projection. A job's own PNG output takes the
    // canvas over without a file of its own to export, so the action falls back to disabled.
    private bool _downloadablePreview;
    private string? _calibratedTrackPath;
    private string? _calibratedIsoPath;
    private string? _calibratedBluRayPath;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DiscFamilyLabel), nameof(IsDvdSelected), nameof(IsCdSelected),
        nameof(IsBluRaySelected))]
    private OpticalDiscFamily _selectedFamily = OpticalDiscFamily.CompactDisc;

    public bool IsCdSelected => SelectedFamily == OpticalDiscFamily.CompactDisc;

    public bool IsDvdSelected => SelectedFamily == OpticalDiscFamily.Dvd;

    public bool IsBluRaySelected => SelectedFamily == OpticalDiscFamily.BluRay;

    /// <summary>False renders the generated artifact (read-back simulation), true renders the
    /// source picture through the generated/measured parameter pair.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsReadbackPreview))]
    private bool _isCalibrationPreview = true;

    /// <summary>Inverse of <see cref="IsCalibrationPreview"/>, so the view can trigger its
    /// read-back panels without an inverse-binding converter. Settable because the input switch
    /// puts this mode on its right-hand option, and the switch's <c>IsSecondSelected</c> is
    /// two-way: a write lands here and flips the single source of truth.</summary>
    public bool IsReadbackPreview
    {
        get => !IsCalibrationPreview;
        set => IsCalibrationPreview = !value;
    }

    [ObservableProperty]
    private string _resultPreviewTitle = "Preview - CD";

    [ObservableProperty]
    private string _resultPreviewPath = string.Empty;

    [ObservableProperty]
    private string _resultPreviewPlaceholderText = "Please select generated CD track or ISO.";

    [ObservableProperty]
    private string _statusText = "Waiting for generated CD track or ISO";

    [ObservableProperty]
    private System.Windows.Media.Imaging.BitmapSource? _resultImage;

    [ObservableProperty]
    private bool _hasResultImage;

    [ObservableProperty]
    private bool _hasFileRegionOverlay;

    public LivePreviewViewModel(Dispatcher dispatcher, ShellViewModel shell, DiscParametersState state)
    {
        _shell = shell;
        _state = state;
        Inspector = new PreviewInspectorViewModel(shell, state);
        _state.PropertyChanged += State_PropertyChanged;
        _livePreviewTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(450),
        };
        _livePreviewTimer.Tick += (_, _) =>
        {
            _livePreviewTimer.Stop();
            _ = RefreshAsync();
        };
        RefreshCommand = new RelayCommand(RefreshNow);
        DownloadPreviewCommand = new RelayCommand(DownloadPreview, CanDownloadPreview);
    }

    internal bool IsLivePreviewReady => _isLivePreviewReady;

    public RelayCommand RefreshCommand { get; }

    /// <summary>Writes the current preview next to the picture it was derived from.</summary>
    public RelayCommand DownloadPreviewCommand { get; }

    /// <summary>Picks the generated artifact to read back, matching the export page's pickers.</summary>
    public RelayCommand BrowseCdTrackCommand => _shell.BrowseCdTrackCommand;

    public RelayCommand BrowseDvdIsoCommand => _shell.BrowseDvdIsoCommand;

    /// <summary>
    /// Switching to another artifact adopts the geometry it was generated with, so an imported
    /// track or ISO starts from its own parameters instead of whatever was fitted before.
    /// </summary>
    private void State_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(DiscParametersState.CdTrackPath))
        {
            AdoptTrackMetadata(_state.CdTrackPath.Trim());
            DownloadPreviewCommand.NotifyCanExecuteChanged();
            RaiseEmptyStateAction();
        }
        else if (e.PropertyName == nameof(DiscParametersState.DvdIsoPath))
        {
            AdoptIsoMetadata(_state.DvdIsoPath.Trim());
            DownloadPreviewCommand.NotifyCanExecuteChanged();
            RaiseEmptyStateAction();
        }
        else if (e.PropertyName == nameof(DiscParametersState.BdIsoPath))
        {
            AdoptBluRayMetadata(_state.BdIsoPath.Trim());
            DownloadPreviewCommand.NotifyCanExecuteChanged();
            RaiseEmptyStateAction();
        }
        else if (e.PropertyName is nameof(DiscParametersState.CdImagePath)
            or nameof(DiscParametersState.DvdImagePath)
            or nameof(DiscParametersState.BdImagePath))
        {
            // The calibration preview exports next to its source picture, so editing that path
            // can change whether the export has a destination at all.
            DownloadPreviewCommand.NotifyCanExecuteChanged();
            RaiseEmptyStateAction();
        }
    }

    // ---- Monitor empty state: the placeholder plus the one jump that fills the gap ----

    /// <summary>The workspace that can produce what the empty monitor is waiting for, or null
    /// once there is nothing to wait for (a result is showing, or the input exists but the
    /// picture is held back by something a jump cannot fix, like an invalid parameter).</summary>
    public Workspace? EmptyStateActionWorkspace
    {
        get
        {
            if (HasResultImage)
            {
                return null;
            }

            if (IsCalibrationPreview)
            {
                string imagePath = SelectedFamily switch
                {
                    OpticalDiscFamily.Dvd => _state.DvdImagePath,
                    OpticalDiscFamily.BluRay => _state.BdImagePath,
                    _ => _state.CdImagePath,
                };
                imagePath = imagePath.Trim();
                return imagePath.Length == 0 || !File.Exists(imagePath) ? Workspace.Design : null;
            }

            string artifactPath = SelectedFamily switch
            {
                OpticalDiscFamily.Dvd => _state.DvdIsoPath,
                OpticalDiscFamily.BluRay => _state.BdIsoPath,
                _ => _state.CdTrackPath,
            };
            artifactPath = artifactPath.Trim();
            return artifactPath.Length == 0 || !File.Exists(artifactPath) ? Workspace.Export : null;
        }
    }

    /// <summary>Empty label when no jump applies, which keeps the empty-state card's action
    /// hidden instead of navigating to a null workspace.</summary>
    public string EmptyStateActionLabel => EmptyStateActionWorkspace switch
    {
        Workspace.Design => "Go to Design",
        Workspace.Export => "Go to Export",
        _ => string.Empty,
    };

    private void RaiseEmptyStateAction()
    {
        OnPropertyChanged(nameof(EmptyStateActionWorkspace));
        OnPropertyChanged(nameof(EmptyStateActionLabel));
    }

    private void AdoptTrackMetadata(string trackPath)
    {
        if (trackPath.Length == 0
            || string.Equals(trackPath, _calibratedTrackPath, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _calibratedTrackPath = trackPath;
        CdDataImageMetadata? dataMetadata = CdDataImageMetadata.TryLoad(trackPath);
        if (dataMetadata is not null)
        {
            CdDiscParameters parameters = dataMetadata.Parameters;
            ToneProfile dataTone = dataMetadata.Tone.Normalized();
            _state.CdSectors = parameters.Sectors.ToString(System.Globalization.CultureInfo.InvariantCulture);
            _state.CdInnerRadius = FormatNumber(parameters.InnerRadiusMm);
            _state.CdOuterRadius = FormatNumber(parameters.OuterRadiusMm);
            _state.CdFilesAtOuterRing = dataMetadata.FilesAtOuterRing;
            _state.ToneDitherStrength = dataTone.DitherStrength;
            _state.ToneLevels = dataTone.Levels;
            _state.ToneBrightness = dataTone.Brightness;
            _state.ToneSrgbLinearization = dataTone.SrgbLinearization;
            _shell.AppendLog($"[{DateTime.Now:HH:mm:ss}] Loaded CD data disc geometry and file layout from {Path.GetFileName(trackPath)}.\n");
            return;
        }
        CdTrackMetadata? metadata = CdTrackMetadata.TryLoad(trackPath);
        if (metadata is null)
        {
            return;
        }

        DiscParametersState state = _state;
        string sectors = metadata.Sectors.ToString(System.Globalization.CultureInfo.InvariantCulture);
        string innerRadius = FormatNumber(metadata.InnerRadiusMm);
        string outerRadius = FormatNumber(metadata.OuterRadiusMm);
        ToneProfile tone = new ToneProfile(
            metadata.ToneDitherStrength,
            metadata.ToneLevels,
            metadata.ToneBrightness,
            metadata.ToneSrgbLinearization).Normalized();
        if (state.CdSectors.Trim() == sectors
            && state.CdInnerRadius.Trim() == innerRadius
            && state.CdOuterRadius.Trim() == outerRadius
            && state.CdInterleave == metadata.Interleaved
            && state.ToneDitherStrength == tone.DitherStrength
            && state.ToneLevels == tone.Levels
            && state.ToneBrightness == tone.Brightness
            && state.ToneSrgbLinearization == tone.SrgbLinearization)
        {
            return;
        }

        state.CdSectors = sectors;
        state.CdInnerRadius = innerRadius;
        state.CdOuterRadius = outerRadius;
        state.CdInterleave = metadata.Interleaved;
        state.ToneDitherStrength = tone.DitherStrength;
        state.ToneLevels = tone.Levels;
        state.ToneBrightness = tone.Brightness;
        state.ToneSrgbLinearization = tone.SrgbLinearization;
        _shell.AppendLog(
            $"[{DateTime.Now:HH:mm:ss}] Loaded calibration geometry from generation parameters of {System.IO.Path.GetFileName(trackPath)}: "
            + $"r0={innerRadius} mm, r1={outerRadius} mm, {sectors} sectors, interleave={metadata.Interleaved}，"
            + $"tone={tone.Levels} levels/{tone.DitherStrength * 100:0}% dither.\n");
    }

    private void AdoptIsoMetadata(string isoPath)
    {
        if (isoPath.Length == 0
            || string.Equals(isoPath, _calibratedIsoPath, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _calibratedIsoPath = isoPath;
        DvdImageMetadata? metadata = DvdImageMetadata.TryLoad(isoPath);
        if (metadata is null)
        {
            return;
        }

        DiscParametersState state = _state;
        string totalSectors = metadata.TotalSectors.ToString(
            System.Globalization.CultureInfo.InvariantCulture);
        string innerRadius = FormatNumber(metadata.InnerRadiusMm);
        string outerRadius = FormatNumber(metadata.OuterRadiusMm);
        if (state.DvdTotalSectors.Trim() == totalSectors
            && state.DvdInnerRadius.Trim() == innerRadius
            && state.DvdOuterRadius.Trim() == outerRadius
            && state.DvdPitchLinear.Trim() == FormatNumber(metadata.PitchLinear)
            && state.DvdPitchQuadratic.Trim() == FormatNumber(metadata.PitchQuadratic)
            && state.DvdPitchCubic.Trim() == FormatNumber(metadata.PitchCubic)
            && state.DvdFilesAtOuterRing == metadata.FilesAtOuterRing)
        {
            return;
        }

        state.DvdTotalSectors = totalSectors;
        state.DvdInnerRadius = innerRadius;
        state.DvdOuterRadius = outerRadius;
        state.DvdPitchLinear = FormatNumber(metadata.PitchLinear);
        state.DvdPitchQuadratic = FormatNumber(metadata.PitchQuadratic);
        state.DvdPitchCubic = FormatNumber(metadata.PitchCubic);
        state.DvdFilesAtOuterRing = metadata.FilesAtOuterRing;
        _shell.AppendLog(
            $"[{DateTime.Now:HH:mm:ss}] Loaded calibration geometry from generation parameters of {System.IO.Path.GetFileName(isoPath)}: "
            + $"r0={innerRadius} mm, r1={outerRadius} mm, {totalSectors} total sectors; {state.DvdPitchSummary}\n");
    }

    /// <summary>The BD sidecar restores the geometry, drawing range and mapping polarity used
    /// to produce the ISO. Without it the current project geometry stays in place and the
    /// projection still works, but the file may have been made with different parameters.</summary>
    private void AdoptBluRayMetadata(string isoPath)
    {
        if (isoPath.Length == 0
            || string.Equals(isoPath, _calibratedBluRayPath, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _calibratedBluRayPath = isoPath;
        BluRayIsoMetadata? metadata = BluRayIsoMetadata.TryLoad(isoPath);
        if (metadata is null)
        {
            _shell.AppendLog(
                $"[{DateTime.Now:HH:mm:ss}] {System.IO.Path.GetFileName(isoPath)} has no available Blu-ray sidecar, "
                + "preview continues using current project geometry.\n");
            return;
        }

        DiscParametersState state = _state;
        string sectors = metadata.TotalSectors.ToString(
            System.Globalization.CultureInfo.InvariantCulture);
        string innerRadius = FormatNumber(metadata.InnerRadiusMm);
        string outerRadius = FormatNumber(metadata.OuterRadiusMm);
        state.BdTotalSectors = sectors;
        state.BdInnerRadius = innerRadius;
        state.BdOuterRadius = outerRadius;
        state.BdActualInnerRadius = innerRadius;
        state.BdActualOuterRadius = outerRadius;
        state.BdInvertMapping = metadata.InvertMapping;
        state.BdFilesAtOuterRing = metadata.FilesAtOuterRing;
        state.BdDrawingStartSector = metadata.DrawingStartSector.ToString(
            System.Globalization.CultureInfo.InvariantCulture);
        state.BdDrawingEndSectorExclusive = metadata.DrawingEndSectorExclusive.ToString(
            System.Globalization.CultureInfo.InvariantCulture);
        state.ToneDitherStrength = metadata.Tone.DitherStrength;
        state.ToneLevels = ToneProfile.TwoLevels;
        state.ToneBrightness = metadata.Tone.Brightness;
        state.ToneSrgbLinearization = metadata.Tone.SrgbLinearization;
        _shell.AppendLog(
            $"[{DateTime.Now:HH:mm:ss}] Loaded geometry from sidecar of {System.IO.Path.GetFileName(isoPath)}: "
            + $"r0={innerRadius} mm, r1={outerRadius} mm, {sectors} sectors, "
            + $"drawing {metadata.DrawingStartSector}..{metadata.DrawingEndSectorExclusive - 1}, "
            + $"polarity={(metadata.InvertMapping ? "Invert 3T/6T" : "Standard 3T/6T")}.\n");
    }

    private static string FormatNumber(double value)
        => value.ToString("R", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>Disc-type selection: clear the stale canvas and reschedule.</summary>
    partial void OnSelectedFamilyChanged(OpticalDiscFamily value)
    {
        if (_suppressDiscTypeAutoUpdate)
        {
            return;
        }

        UpdateMode(clearResult: true);
        Schedule();
    }

    /// <summary>Configures the disc type without clearing the canvas (snapshot entry point).</summary>
    internal void SetDiscType(OpticalDiscFamily family, bool clearResult)
    {
        _suppressDiscTypeAutoUpdate = true;
        try
        {
            SelectedFamily = family;
        }
        finally
        {
            _suppressDiscTypeAutoUpdate = false;
        }

        UpdateMode(clearResult);
    }

    internal void SetDiscType(bool dvd, bool clearResult)
        => SetDiscType(dvd ? OpticalDiscFamily.Dvd : OpticalDiscFamily.CompactDisc, clearResult);

    /// <summary>Preview-mode semantics: the input changes, so the stale canvas goes and the
    /// new pipeline runs.</summary>
    partial void OnIsCalibrationPreviewChanged(bool value)
    {
        if (_suppressPreviewModeAutoUpdate)
        {
            return;
        }

        UpdateMode(clearResult: true);
        Schedule();
    }

    /// <summary>Configures the preview mode without clearing the canvas (snapshot entry point).</summary>
    internal void SetPreviewMode(bool calibration, bool clearResult)
    {
        _suppressPreviewModeAutoUpdate = true;
        try
        {
            IsCalibrationPreview = calibration;
        }
        finally
        {
            _suppressPreviewModeAutoUpdate = false;
        }

        UpdateMode(clearResult);
    }

    internal void OnWindowLoaded()
    {
        UpdateMode(clearResult: false);
        Schedule();
    }

    internal void UpdateMode(bool clearResult)
    {
        HasFileRegionOverlay = false;
        // The inspector is a lens over both families; every mode change routes through here, so
        // this is where it is re-pointed.
        Inspector.SetFamily(SelectedFamily);
        string discName = DiscFamilyLabel;
        string inputName = IsCalibrationPreview
            ? $"{discName} Source Image"
            : SelectedFamily switch
            {
                OpticalDiscFamily.Dvd => "Generated DVD ISO",
                OpticalDiscFamily.BluRay => "Generated Blu-ray ISO",
                _ => "Generated CD Audio Track or ISO",
            };
        ResultPreviewTitle = $"Preview - {discName}";
        ResultPreviewPlaceholderText = $"Please select {inputName} first.";
        StatusText = $"Waiting for {inputName}";
        RaiseEmptyStateAction();

        if (!clearResult)
        {
            return;
        }

        ResultImage = null;
        HasResultImage = false;
        ResultPreviewPath = string.Empty;
        _downloadablePreview = false;
        DownloadPreviewCommand.NotifyCanExecuteChanged();
        string? previousPath = _lastLivePreviewPath;
        _lastLivePreviewPath = null;
        TempFiles.TryDeleteTemporaryFile(previousPath);
    }

    internal void Schedule()
    {
        HasFileRegionOverlay = false;
        if (!_shell.IsWindowLoaded)
        {
            return;
        }

        _livePreviewCancellation?.Cancel();
        _livePreviewTimer.Stop();
        _livePreviewRevision++;
        _isLivePreviewReady = false;
        StatusText = "Parameters changed, preparing refresh…";
        _livePreviewTimer.Start();
    }

    internal void RefreshNow()
    {
        HasFileRegionOverlay = false;
        _livePreviewTimer.Stop();
        _livePreviewCancellation?.Cancel();
        _livePreviewRevision++;
        _ = RefreshAsync();
    }

    private async Task RefreshAsync()
    {
        if (_livePreviewRefreshRunning)
        {
            _livePreviewRefreshPending = true;
            return;
        }

        _livePreviewRefreshRunning = true;
        try
        {
            do
            {
                HasFileRegionOverlay = false;
                _livePreviewRefreshPending = false;
                int revision = _livePreviewRevision;
                switch (SelectedFamily)
                {
                    case OpticalDiscFamily.Dvd:
                        await RefreshDvdAsync(revision);
                        break;
                    case OpticalDiscFamily.BluRay:
                        await RefreshBluRayAsync(revision);
                        break;
                    default:
                        await RefreshCdAsync(revision);
                        break;
                }

                if (revision != _livePreviewRevision)
                {
                    _livePreviewRefreshPending = true;
                }
            }
            while (_livePreviewRefreshPending && _shell.IsWindowLoaded);
        }
        finally
        {
            _livePreviewRefreshRunning = false;
        }
    }

    private async Task RefreshCdAsync(int revision)
    {
        _livePreviewCancellation?.Cancel();
        _livePreviewCancellation?.Dispose();
        CancellationTokenSource cancellation = new();
        _livePreviewCancellation = cancellation;

        if (IsCalibrationPreview)
        {
            await RefreshCdCalibrationAsync(revision, cancellation.Token);
        }
        else
        {
            await RefreshCdReadbackAsync(revision, cancellation.Token);
        }
    }

    /// <summary>Read-back simulation: undo the generation's delay interleave, then map the
    /// logical bytes onto the spiral described by the calibration geometry.</summary>
    private async Task RefreshCdReadbackAsync(int revision, CancellationToken cancellationToken)
    {
        if (!TryReadCdSettings(
                out string trackPath,
                out CdDiscParameters parameters,
                out int outputSize,
                out int samplesPerSector,
                out string message))
        {
            StatusText = message;
            if (!HasResultImage)
            {
                ResultPreviewPlaceholderText = message;
            }

            return;
        }

        bool deinterleave = _state.CdInterleave;
        bool dataImage = Path.GetExtension(trackPath).Equals(".iso", StringComparison.OrdinalIgnoreCase);
        Directory.CreateDirectory(TempFiles.LivePreviewDirectory);
        string outputPath = Path.Combine(
            TempFiles.LivePreviewDirectory,
            $"cd-live-preview-{revision}.png");
        StatusText = $"Refreshing {outputSize} px live preview…";
        bool keepOutput = false;
        try
        {
            CdDataImageMetadata? metadata = dataImage ? CdDataImageMetadata.TryLoad(trackPath) : null;
            if (metadata is not null)
            {
                parameters = metadata.Parameters with
                {
                    InnerRadiusMm = parameters.InnerRadiusMm,
                    OuterRadiusMm = parameters.OuterRadiusMm,
                    Sectors = parameters.Sectors,
                };
            }
            bool overlay = metadata is not null && metadata.Parameters.Sectors == parameters.Sectors;
            await StaWorker.RunAsync(() =>
            {
                if (dataImage)
                {
                    CdDataImageGenerator.PreviewTrack(trackPath, outputPath, parameters,
                        outputSize, 2048 / samplesPerSector, cancellationToken);
                    if (overlay)
                        FileRegionPreviewOverlay.ApplyCd(outputPath, parameters,
                            metadata!.DrawingStartLba, metadata.DrawingEndLbaExclusive, cancellationToken);
                }
                else
                {
                    CdTrackGenerator.PreviewTrack(
                        trackPath, outputPath, parameters, outputSize,
                        CdDiscParameters.BytesPerSector / samplesPerSector, deinterleave, cancellationToken);
                }
                return 0;
            });
            cancellationToken.ThrowIfCancellationRequested();
            if (revision != _livePreviewRevision)
            {
                return;
            }

            PublishPreview(outputPath, trackPath, "Preview - CD", overlay);
            keepOutput = true;
            StatusText = $"Updated in real time · Readback simulation · {(dataImage ? "CD Data Disc" : deinterleave ? "De-interleave" : "Audio Track")}"
                + $" · {outputSize} px · {samplesPerSector} samples/sector";
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            StatusText = $"Live preview failed: {exception.Message}";
            _shell.AppendLog($"[{DateTime.Now:HH:mm:ss}] Live preview failed: {exception}\n");
        }
        finally
        {
            if (!keepOutput)
            {
                TempFiles.TryDeleteTemporaryFile(outputPath);
            }
        }
    }

    /// <summary>Calibration projection: the source picture is sampled where the generated
    /// geometry puts it and drawn where the measured geometry reads it. Only the source image
    /// is read, so a refresh costs the picture's size rather than the track's.</summary>
    private async Task RefreshCdCalibrationAsync(int revision, CancellationToken cancellationToken)
    {
        if (!TryReadCdWarpSettings(
                out string imagePath,
                out CdDiscParameters generated,
                out CdDiscParameters actual,
                out int outputSize,
                out int samplesPerSector,
                out ToneProfile tone,
                out string message))
        {
            StatusText = message;
            if (!HasResultImage)
            {
                ResultPreviewPlaceholderText = message;
            }

            return;
        }

        Directory.CreateDirectory(TempFiles.LivePreviewDirectory);
        string outputPath = Path.Combine(
            TempFiles.LivePreviewDirectory,
            $"cd-live-warp-{revision}.png");
        StatusText = $"Refreshing {outputSize} px calibration preview…";
        bool keepOutput = false;
        try
        {
            string dataDirectory = _state.CdDataDirectory.Trim();
            string volumeLabel = _state.CdVolumeLabel.Trim();
            bool filesAtOuterRing = _state.CdFilesAtOuterRing;
            CdDataLayoutPlan? plan = await StaWorker.RunAsync(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                CdDataLayoutPlan? layout = dataDirectory.Length > 0
                    ? CdDataImageGenerator.PrepareLayout(dataDirectory, generated, volumeLabel, filesAtOuterRing)
                    : null;
                cancellationToken.ThrowIfCancellationRequested();
                if (layout is not null)
                {
                    CdDataImageGenerator.PreviewWarp(imagePath, outputPath, generated, actual,
                        outputSize, samplesPerSector, tone, layout, cancellationToken);
                    FileRegionPreviewOverlay.ApplyCd(outputPath, actual,
                        layout.DrawingStartLba, layout.DrawingEndLbaExclusive, cancellationToken);
                }
                else
                {
                    CdTrackGenerator.PreviewWarp(imagePath, outputPath, generated, actual,
                        outputSize, samplesPerSector, tone, cancellationToken);
                }
                return layout;
            });
            cancellationToken.ThrowIfCancellationRequested();
            if (revision != _livePreviewRevision)
            {
                return;
            }

            PublishPreview(outputPath, imagePath, "Preview - CD", plan is not null);
            keepOutput = true;
            StatusText = $"Updated in real time · Calibration preview · Grayscale {tone.Levels} levels"
                + $" · Dither {tone.DitherStrength * 100:0}% · {outputSize} px"
                + $" · {samplesPerSector} samples/sector";
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            StatusText = $"Live preview failed: {exception.Message}";
            _shell.AppendLog($"[{DateTime.Now:HH:mm:ss}] Live preview failed: {exception}\n");
        }
        finally
        {
            if (!keepOutput)
            {
                TempFiles.TryDeleteTemporaryFile(outputPath);
            }
        }
    }

    private async Task RefreshDvdAsync(int revision)
    {
        _livePreviewCancellation?.Cancel();
        _livePreviewCancellation?.Dispose();
        CancellationTokenSource cancellation = new();
        _livePreviewCancellation = cancellation;

        if (IsCalibrationPreview)
        {
            await RefreshDvdCalibrationAsync(revision, cancellation.Token);
        }
        else
        {
            await RefreshDvdReadbackAsync(revision, cancellation.Token);
        }
    }

    /// <summary>Read-back simulation: the solver classifies the generated ISO's payload
    /// bytes and splats them under the measured disc geometry.</summary>
    private async Task RefreshDvdReadbackAsync(int revision, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(TempFiles.LivePreviewDirectory);
        string outputPath = Path.Combine(
            TempFiles.LivePreviewDirectory,
            $"dvd-live-preview-{revision}.png");
        string summaryJsonPath = outputPath + ".json";
        if (!TryBuildDvdCommand(
                outputPath,
                out string isoPath,
                out string[] arguments,
                out int outputSize,
                out int samplesPerSector,
                out string message))
        {
            StatusText = message;
            if (!HasResultImage)
            {
                ResultPreviewPlaceholderText = message;
            }

            return;
        }

        StatusText = $"Refreshing {outputSize} px DVD live preview…";
        bool keepOutput = false;
        try
        {
            DvdImageMetadata? metadata = DvdImageMetadata.TryLoad(isoPath);
            DvdStreamingOptions actual = ReadDvdPreviewGeometry(calibration: false);
            bool overlay = metadata is { HasFileSystem: true };
            int exitCode = await StaWorker.RunAsync(() =>
            {
                int result = UnifiedCommandRunner.Run(arguments);
                if (result == 0 && overlay)
                    FileRegionPreviewOverlay.ApplyDvd(outputPath, actual,
                        metadata!.DrawingStartLba, metadata.DrawingEndLbaExclusive, cancellationToken);
                return result;
            });
            if (exitCode != 0)
            {
                throw new InvalidOperationException("DVD readback simulation engine failed to complete rendering.");
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (revision != _livePreviewRevision)
            {
                return;
            }

            PublishPreview(outputPath, isoPath, "Preview - DVD", overlay);
            keepOutput = true;
            StatusText = $"Updated in real time · Readback simulation · CW · {outputSize} px · {samplesPerSector} samples/sector";
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            StatusText = $"Live preview failed: {exception.Message}";
            _shell.AppendLog($"[{DateTime.Now:HH:mm:ss}] DVD Live preview failed: {exception}\n");
        }
        finally
        {
            TryDeleteFile(summaryJsonPath);
            if (!keepOutput)
            {
                TempFiles.TryDeleteTemporaryFile(outputPath);
            }
        }
    }

    /// <summary>Calibration projection: the solver renders the source picture at the
    /// generated radii and projects it onto the measured ones.</summary>
    private async Task RefreshDvdCalibrationAsync(int revision, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(TempFiles.LivePreviewDirectory);
        string outputPath = Path.Combine(
            TempFiles.LivePreviewDirectory,
            $"dvd-live-warp-{revision}.png");
        string summaryJsonPath = outputPath + ".json";
        if (!TryBuildDvdCalibrateCommand(
                outputPath,
                out string imagePath,
                out string[] arguments,
                out int outputSize,
                out int samplesPerSector,
                out DvdHybridStreamingPlan? filePlan,
                out string message))
        {
            StatusText = message;
            if (!HasResultImage)
            {
                ResultPreviewPlaceholderText = message;
            }

            return;
        }

        StatusText = $"Refreshing {outputSize} px DVD calibration preview…";
        bool keepOutput = false;
        try
        {
            DvdStreamingOptions actual = ReadDvdPreviewGeometry(calibration: true);
            int exitCode = await StaWorker.RunAsync(() =>
            {
                int result = UnifiedCommandRunner.Run(arguments);
                if (result == 0 && filePlan is not null)
                    FileRegionPreviewOverlay.ApplyDvd(outputPath, actual,
                        filePlan.DrawingStartLba, filePlan.DrawingEndLbaExclusive, cancellationToken);
                return result;
            });
            if (exitCode != 0)
            {
                throw new InvalidOperationException("DVD calibration preview engine failed to complete rendering.");
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (revision != _livePreviewRevision)
            {
                return;
            }

            PublishPreview(outputPath, imagePath, "Preview - DVD", filePlan is not null);
            keepOutput = true;
            StatusText = $"Updated in real time · Calibration preview · CW · Dither {_state.DvdTone.DitherStrength * 100:0}%"
                + $" · {outputSize} px · {samplesPerSector} samples/sector";
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            StatusText = $"Live preview failed: {exception.Message}";
            _shell.AppendLog($"[{DateTime.Now:HH:mm:ss}] DVD Live preview failed: {exception}\n");
        }
        finally
        {
            TryDeleteFile(summaryJsonPath);
            if (!keepOutput)
            {
                TempFiles.TryDeleteTemporaryFile(outputPath);
            }
        }
    }

    private async Task RefreshBluRayAsync(int revision)
    {
        _livePreviewCancellation?.Cancel();
        _livePreviewCancellation?.Dispose();
        CancellationTokenSource cancellation = new();
        _livePreviewCancellation = cancellation;

        if (IsCalibrationPreview)
        {
            await RefreshBluRayProjectionAsync(revision, cancellation.Token);
        }
        else
        {
            await RefreshBluRayReadbackAsync(revision, cancellation.Token);
        }
    }

    /// <summary>Design projection: the source picture is quantized once by the tone profile,
    /// laid out at the generated geometry and projected onto the measured one. Polarity is
    /// part of the generation model, so this side of the preview applies it.</summary>
    private async Task RefreshBluRayProjectionAsync(int revision, CancellationToken cancellationToken)
    {
        string imagePath = _state.BdImagePath.Trim();
        if (imagePath.Length == 0 || !File.Exists(imagePath))
        {
            string message = imagePath.Length == 0
                ? "Please import an image on Design page; composition will be filled automatically."
                : "Blu-ray source image does not exist.";
            StatusText = message;
            if (!HasResultImage)
            {
                ResultPreviewPlaceholderText = message;
            }

            return;
        }

        BluRayDiscParameters generated, actual;
        int outputSize;
        ToneProfile tone;
        bool invert;
        BluRayIsoPlan plan;
        try
        {
            generated = ReadBluRayDisc(_state.BdTotalSectors, _state.BdInnerRadius, _state.BdOuterRadius);
            actual = ReadBluRayDisc(_state.BdTotalSectors,
                _state.BdActualInnerRadius, _state.BdActualOuterRadius);
            outputSize = Math.Clamp(
                ParameterParser.ParsePositiveInt(_state.BdPreviewSize, "Preview Size"), 64, 8192);
            tone = _state.BluRayTone;
            invert = _state.BdInvertMapping;
            plan = PrepareBluRayProjectionPlan(generated);
        }
        catch (Exception exception) when (exception is FormatException
                                              or OverflowException
                                              or ArgumentException
                                              or InvalidOperationException
                                              or IOException
                                              or UnauthorizedAccessException)
        {
            StatusText = exception.Message;
            if (!HasResultImage)
            {
                ResultPreviewPlaceholderText = exception.Message;
            }

            return;
        }

        Directory.CreateDirectory(TempFiles.LivePreviewDirectory);
        string outputPath = Path.Combine(
            TempFiles.LivePreviewDirectory,
            $"bd-live-projection-{revision}.png");
        StatusText = $"Refreshing {outputSize} px Blu-ray design projection…";
        bool keepOutput = false;
        try
        {
            await StaWorker.RunAsync(() =>
            {
                BluRayPreviewRenderer.RenderCalibration(
                    imagePath,
                    outputPath,
                    generated,
                    actual,
                    plan.DrawingStartSector,
                    plan.DrawingEndSectorExclusive,
                    outputSize,
                    invert,
                    cancellationToken,
                    tone);
                if (plan.HasFileSystem)
                    FileRegionPreviewOverlay.ApplyBluRay(outputPath, actual,
                        plan.DrawingStartSector, plan.DrawingEndSectorExclusive, cancellationToken);
                return 0;
            });
            cancellationToken.ThrowIfCancellationRequested();
            if (revision != _livePreviewRevision)
            {
                return;
            }

            PublishPreview(outputPath, imagePath, "Preview - BD", plan.HasFileSystem);
            keepOutput = true;
            StatusText = $"Updated in real time · Design projection · 3T/6T map"
                + $" · {(invert ? "Invert 3T/6T" : "Standard 3T/6T")}"
                + $" · {(tone.SrgbLinearization ? "sRGB Linearization · " : string.Empty)}{outputSize} px";
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            StatusText = $"Live preview failed: {exception.Message}";
            _shell.AppendLog($"[{DateTime.Now:HH:mm:ss}] Blu-ray live preview failed: {exception}\n");
        }
        finally
        {
            if (!keepOutput)
            {
                TempFiles.TryDeleteTemporaryFile(outputPath);
            }
        }
    }

    /// <summary>Finished-artifact read-back: classify the ISO's payload bytes as AA/44 and
    /// splat them under the measured geometry. Polarity is not applied again: the preview
    /// shows the bytes on the disc, which is a 3T/6T class map rather than reflectance.</summary>
    private async Task RefreshBluRayReadbackAsync(int revision, CancellationToken cancellationToken)
    {
        string isoPath = _state.BdIsoPath.Trim();
        if (isoPath.Length == 0 || !File.Exists(isoPath))
        {
            string message = isoPath.Length == 0
                ? "Please select generated Blu-ray ISO (automatically filled after exporting)."
                : "Blu-ray ISO file does not exist.";
            StatusText = message;
            if (!HasResultImage)
            {
                ResultPreviewPlaceholderText = message;
            }

            return;
        }

        BluRayDiscParameters actual;
        int outputSize;
        try
        {
            actual = ReadBluRayDisc(_state.BdTotalSectors,
                _state.BdActualInnerRadius, _state.BdActualOuterRadius);
            outputSize = Math.Clamp(
                ParameterParser.ParsePositiveInt(_state.BdPreviewSize, "Preview Size"), 64, 8192);
        }
        catch (Exception exception) when (exception is FormatException
                                              or OverflowException
                                              or ArgumentException)
        {
            StatusText = exception.Message;
            if (!HasResultImage)
            {
                ResultPreviewPlaceholderText = exception.Message;
            }

            return;
        }

        Directory.CreateDirectory(TempFiles.LivePreviewDirectory);
        string outputPath = Path.Combine(
            TempFiles.LivePreviewDirectory,
            $"bd-live-readback-{revision}.png");
        StatusText = $"Refreshing {outputSize} px Blu-ray readback…";
        bool keepOutput = false;
        try
        {
            BluRayIsoMetadata? metadata = BluRayIsoMetadata.TryLoad(isoPath);
            bool overlay = metadata?.UdfTimestamp is not null;
            await StaWorker.RunAsync(() =>
            {
                BluRayPreviewRenderer.RenderReadback(
                    isoPath, outputPath, actual, outputSize, cancellationToken,
                    metadata?.DrawingStartSector ?? 0, metadata?.DrawingEndSectorExclusive);
                if (overlay)
                    FileRegionPreviewOverlay.ApplyBluRay(outputPath, actual,
                        metadata!.DrawingStartSector, metadata.DrawingEndSectorExclusive, cancellationToken);
                return 0;
            });
            cancellationToken.ThrowIfCancellationRequested();
            if (revision != _livePreviewRevision)
            {
                return;
            }

            PublishPreview(outputPath, isoPath, "Preview - BD", overlay);
            keepOutput = true;
            StatusText = $"Updated in real time · Image readback · 3T/6T map (non-reflective) · {outputSize} px";
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            StatusText = $"Live preview failed: {exception.Message}";
            _shell.AppendLog($"[{DateTime.Now:HH:mm:ss}] Blu-ray readback failed: {exception}\n");
        }
        finally
        {
            if (!keepOutput)
            {
                TempFiles.TryDeleteTemporaryFile(outputPath);
            }
        }
    }

    /// <summary>Project projection always follows the current folder and placement. A
    /// sidecar range belongs to its finished ISO and must not override later edits.</summary>
    internal BluRayIsoPlan PrepareBluRayProjectionPlan(BluRayDiscParameters generated)
    {
        string dataDirectory = _state.BdDataDirectory.Trim();
        return BluRayIsoGenerator.PrepareDrawing(generated,
            dataDirectory.Length == 0 ? null : dataDirectory,
            string.IsNullOrWhiteSpace(_state.BdVolumeLabel) ? "BD_IMAGE" : _state.BdVolumeLabel.Trim(),
            filesAtOuterRing: dataDirectory.Length > 0 && _state.BdFilesAtOuterRing);
    }

    private DvdStreamingOptions ReadDvdPreviewGeometry(bool calibration)
    {
        uint sectors = checked((uint)ParameterParser.ParsePositiveLong(_state.DvdTotalSectors, "Total Sectors"));
        DvdStreamingOptions options = new(sectors,
            ParameterParser.ParsePositiveDouble(calibration ? _state.DvdActualInnerRadius : _state.DvdInnerRadius, "Inner Radius"),
            ParameterParser.ParsePositiveDouble(calibration ? _state.DvdActualOuterRadius : _state.DvdOuterRadius, "Outer Radius"),
            PitchLinear: ParameterParser.ParseDouble(calibration ? _state.DvdActualPitchLinear : _state.DvdPitchLinear, "Linear Term"),
            PitchQuadratic: ParameterParser.ParseDouble(calibration ? _state.DvdActualPitchQuadratic : _state.DvdPitchQuadratic, "Quadratic Term"),
            PitchCubic: ParameterParser.ParseDouble(calibration ? _state.DvdActualPitchCubic : _state.DvdPitchCubic, "Cubic Term"));
        options.Validate();
        return options;
    }

    private static BluRayDiscParameters ReadBluRayDisc(
        string sectorsText, string innerText, string outerText)
    {
        long sectors = ParameterParser.ParsePositiveLong(sectorsText, "Blu-ray Total Sectors");
        if (sectors > uint.MaxValue)
        {
            throw new ArgumentOutOfRangeException("Blu-ray Total Sectors", "Total sectors exceeds Blu-ray engine supported range.");
        }

        BluRayDiscParameters disc = new(
            checked((uint)sectors),
            1,
            ParameterParser.ParsePositiveDouble(innerText, "Blu-ray Inner Radius"),
            ParameterParser.ParsePositiveDouble(outerText, "Blu-ray Outer Radius"));
        disc.Validate();
        return disc;
    }

    /// <summary>Adopts a rendered preview as the canvas content and the export source, and
    /// retires the previous temporary file.</summary>
    private void PublishPreview(string outputPath, string inputPath, string title,
        bool hasFileRegionOverlay = false)
    {
        string? previousPath = _lastLivePreviewPath;
        _lastLivePreviewPath = outputPath;
        _downloadablePreview = true;
        bool loaded = ShowResultPreview(outputPath);
        HasFileRegionOverlay = hasFileRegionOverlay && loaded;
        _isLivePreviewReady = true;
        ResultPreviewTitle = title;
        ResultPreviewPath = Path.GetFileName(inputPath);
        TempFiles.TryDeleteTemporaryFile(previousPath);
    }

    private bool TryReadCdSettings(
        out string trackPath,
        out CdDiscParameters parameters,
        out int outputSize,
        out int samplesPerSector,
        out string message)
    {
        trackPath = _state.CdTrackPath.Trim();
        parameters = null!;
        outputSize = 0;
        samplesPerSector = 0;
        if (trackPath.Length == 0)
        {
            message = "Please select generated CD track or ISO (automatically filled after exporting).";
            return false;
        }

        if (!File.Exists(trackPath))
        {
            message = "CD audio track or ISO file does not exist.";
            return false;
        }

        try
        {
            long sectors = ParameterParser.ParsePositiveLong(_state.CdSectors, "Sectors");
            parameters = new CdDiscParameters(
                ParameterParser.ParsePositiveDouble(_state.CdInnerRadius, "Inner Radius"),
                ParameterParser.ParsePositiveDouble(_state.CdOuterRadius, "Outer Radius"),
                sectors);
            parameters.Validate();

            int configuredSize = ParameterParser.ParsePositiveInt(_state.CdPreviewSize, "Preview Size");
            int configuredSamples = ParameterParser.ParsePositiveInt(_state.CdSamplesPerSector, "Samples per Sector");
            outputSize = Math.Clamp(configuredSize, 256, 1600);
            samplesPerSector = Math.Clamp(configuredSamples, 1, 32);

            message = string.Empty;
            return true;
        }
        catch (Exception exception) when (exception is FormatException or OverflowException or ArgumentOutOfRangeException)
        {
            message = exception.Message;
            return false;
        }
    }

    /// <summary>
    /// Calibration variables of the CD projection. Both parameter sets share the program's
    /// sector count; velocity, start angle and the image outer radius stay the physical
    /// constants of the scheme, as in the read-back path.
    /// </summary>
    private bool TryReadCdWarpSettings(
        out string imagePath,
        out CdDiscParameters generated,
        out CdDiscParameters actual,
        out int outputSize,
        out int samplesPerSector,
        out ToneProfile tone,
        out string message)
    {
        imagePath = _state.CdImagePath.Trim();
        generated = null!;
        actual = null!;
        outputSize = 0;
        samplesPerSector = 0;
        tone = _state.Tone;
        if (imagePath.Length == 0)
        {
            message = "Please import an image on Design page; composition will be filled automatically.";
            return false;
        }

        if (!File.Exists(imagePath))
        {
            message = "CD source image does not exist.";
            return false;
        }

        try
        {
            long sectors = ParameterParser.ParsePositiveLong(_state.CdSectors, "Sectors");
            generated = new CdDiscParameters(
                ParameterParser.ParsePositiveDouble(_state.CdInnerRadius, "Generated Inner Radius"),
                ParameterParser.ParsePositiveDouble(_state.CdOuterRadius, "Generated Outer Radius"),
                sectors);
            actual = new CdDiscParameters(
                ParameterParser.ParsePositiveDouble(_state.CdActualInnerRadius, "Actual Inner Radius"),
                ParameterParser.ParsePositiveDouble(_state.CdActualOuterRadius, "Actual Outer Radius"),
                sectors);
            generated.Validate();
            actual.Validate();

            int configuredSize = ParameterParser.ParsePositiveInt(_state.CdPreviewSize, "Preview Size");
            int configuredSamples = ParameterParser.ParsePositiveInt(_state.CdSamplesPerSector, "Samples per Sector");
            outputSize = Math.Clamp(configuredSize, 256, 1600);
            samplesPerSector = Math.Clamp(configuredSamples, 1, 32);

            message = string.Empty;
            return true;
        }
        catch (Exception exception) when (exception is FormatException or OverflowException or ArgumentOutOfRangeException)
        {
            message = exception.Message;
            return false;
        }
    }

    private bool TryBuildDvdCommand(
        string outputPath,
        out string isoPath,
        out string[] arguments,
        out int outputSize,
        out int samplesPerSector,
        out string message)
    {
        isoPath = _state.DvdIsoPath.Trim();
        arguments = [];
        outputSize = 0;
        samplesPerSector = 0;
        if (isoPath.Length == 0)
        {
            message = "Please select generated DVD ISO (automatically filled after exporting).";
            return false;
        }

        if (!File.Exists(isoPath))
        {
            message = "DVD ISO file does not exist.";
            return false;
        }

        try
        {
            long totalSectorsValue = ParameterParser.ParsePositiveLong(_state.DvdTotalSectors, "Total Sectors");
            if (totalSectorsValue > uint.MaxValue)
            {
                throw new ArgumentOutOfRangeException("Total Sectors", "Total sectors exceeds DVD engine supported range.");
            }

            double innerRadius = ParameterParser.ParsePositiveDouble(_state.DvdInnerRadius, "Inner Radius");
            double outerRadius = ParameterParser.ParsePositiveDouble(_state.DvdOuterRadius, "Outer Radius");
            if (innerRadius >= outerRadius)
            {
                throw new ArgumentOutOfRangeException("Radius", "Inner radius must be less than outer radius.");
            }

            outputSize = Math.Clamp(
                ParameterParser.ParsePositiveInt(_state.DvdPreviewSize, "Preview Size"),
                256,
                1600);
            samplesPerSector = Math.Clamp(
                ParameterParser.ParsePositiveInt(_state.DvdSamplesPerSector, "Samples per Sector"),
                1,
                16);

            // The simulate command falls back to the ISO's solve sidecar JSON for the
            // drawing range, so the app only passes the measured calibration geometry.
            arguments =
            [
                "simulate",
                "--iso", isoPath,
                "--output", outputPath,
                "--total-sectors", totalSectorsValue.ToString(System.Globalization.CultureInfo.InvariantCulture),
                "--inner-radius-mm", innerRadius.ToString("R", System.Globalization.CultureInfo.InvariantCulture),
                "--outer-radius-mm", outerRadius.ToString("R", System.Globalization.CultureInfo.InvariantCulture),
                "--pitch-linear", _state.DvdPitchLinear.Trim(),
                "--pitch-quadratic", _state.DvdPitchQuadratic.Trim(),
                "--pitch-cubic", _state.DvdPitchCubic.Trim(),
                "--spiral-direction", "cw",
                "--preview-size", outputSize.ToString(System.Globalization.CultureInfo.InvariantCulture),
                "--samples-per-sector", samplesPerSector.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ];
            message = string.Empty;
            return true;
        }
        catch (Exception exception) when (exception is FormatException or OverflowException or ArgumentOutOfRangeException)
        {
            message = exception.Message;
            return false;
        }
    }

    /// <summary>
    /// Calibration variables of the DVD projection. Unlike the read-back path, the command
    /// carries both radii pairs: the solver lays the picture out at the generated radii and
    /// projects it onto the measured ones. Channel bit (133.3 nm) and start angle (0°) stay
    /// the physical constants of the scheme.
    /// </summary>
    private bool TryBuildDvdCalibrateCommand(
        string outputPath,
        out string imagePath,
        out string[] arguments,
        out int outputSize,
        out int samplesPerSector,
        out DvdHybridStreamingPlan? filePlan,
        out string message)
    {
        imagePath = _state.DvdImagePath.Trim();
        arguments = [];
        outputSize = 0;
        samplesPerSector = 0;
        filePlan = null;
        if (imagePath.Length == 0)
        {
            message = "Please import an image on Design page; composition will be filled automatically.";
            return false;
        }

        if (!File.Exists(imagePath))
        {
            message = "DVD source image does not exist.";
            return false;
        }

        try
        {
            long totalSectorsValue = ParameterParser.ParsePositiveLong(_state.DvdTotalSectors, "Total Sectors");
            if (totalSectorsValue > uint.MaxValue)
            {
                throw new ArgumentOutOfRangeException("Total Sectors", "Total sectors exceeds DVD engine supported range.");
            }

            double generatedInner = ParameterParser.ParsePositiveDouble(_state.DvdInnerRadius, "Generated Inner Radius");
            double generatedOuter = ParameterParser.ParsePositiveDouble(_state.DvdOuterRadius, "Generated Outer Radius");
            double actualInner = ParameterParser.ParsePositiveDouble(_state.DvdActualInnerRadius, "Actual Inner Radius");
            double actualOuter = ParameterParser.ParsePositiveDouble(_state.DvdActualOuterRadius, "Actual Outer Radius");
            if (generatedInner >= generatedOuter || actualInner >= actualOuter)
            {
                throw new ArgumentOutOfRangeException("Radius", "Inner radius must be less than outer radius.");
            }

            outputSize = Math.Clamp(
                ParameterParser.ParsePositiveInt(_state.DvdPreviewSize, "Preview Size"),
                256,
                1600);
            samplesPerSector = Math.Clamp(
                ParameterParser.ParsePositiveInt(_state.DvdSamplesPerSector, "Samples per Sector"),
                1,
                16);

            string totalSectors = totalSectorsValue.ToString(System.Globalization.CultureInfo.InvariantCulture);
            ToneProfile tone = _state.DvdTone;
            uint drawingStart = 0;
            uint drawingSectors = checked((uint)totalSectorsValue);
            string dataDirectory = _state.DvdDataDirectory.Trim();
            if (dataDirectory.Length > 0)
            {
                DvdStreamingOptions generated = ReadDvdPreviewGeometry(calibration: false);
                filePlan = DvdStreamingGenerator.PrepareHybrid(dataDirectory, generated,
                    string.IsNullOrWhiteSpace(_state.DvdVolumeLabel) ? "DISC_IMAGE" : _state.DvdVolumeLabel.Trim(),
                    filesAtOuterRing: _state.DvdFilesAtOuterRing);
                drawingStart = filePlan.DrawingStartLba;
                drawingSectors = filePlan.DrawingSectors;
            }
            arguments =
            [
                "calibrate",
                "--image", imagePath,
                "--output", outputPath,
                "--total-sectors", totalSectors,
                "--lba", drawingStart.ToString(System.Globalization.CultureInfo.InvariantCulture),
                "--fill-sectors", drawingSectors.ToString(System.Globalization.CultureInfo.InvariantCulture),
                "--generated-inner-radius-mm", generatedInner.ToString("R", System.Globalization.CultureInfo.InvariantCulture),
                "--generated-outer-radius-mm", generatedOuter.ToString("R", System.Globalization.CultureInfo.InvariantCulture),
                "--actual-inner-radius-mm", actualInner.ToString("R", System.Globalization.CultureInfo.InvariantCulture),
                "--actual-outer-radius-mm", actualOuter.ToString("R", System.Globalization.CultureInfo.InvariantCulture),
                "--pitch-linear", _state.DvdPitchLinear.Trim(),
                "--pitch-quadratic", _state.DvdPitchQuadratic.Trim(),
                "--pitch-cubic", _state.DvdPitchCubic.Trim(),
                "--actual-pitch-linear", _state.DvdActualPitchLinear.Trim(),
                "--actual-pitch-quadratic", _state.DvdActualPitchQuadratic.Trim(),
                "--actual-pitch-cubic", _state.DvdActualPitchCubic.Trim(),
                "--spiral-direction", "cw",
                "--image-threshold", "128",
                "--alpha-threshold", "1",
                "--tone-dither", tone.DitherStrength.ToString("R", System.Globalization.CultureInfo.InvariantCulture),
                "--tone-levels", "2",
                "--tone-brightness", tone.Brightness.ToString("R", System.Globalization.CultureInfo.InvariantCulture),
                "--tone-gamma", tone.SrgbLinearization.ToString().ToLowerInvariant(),
                "--preview-size", outputSize.ToString(System.Globalization.CultureInfo.InvariantCulture),
                "--samples-per-sector", samplesPerSector.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ];
            message = string.Empty;
            return true;
        }
        catch (Exception exception) when (exception is FormatException or OverflowException
                                              or ArgumentException or IOException or UnauthorizedAccessException)
        {
            message = exception.Message;
            return false;
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
        }
    }

    internal void PrepareResultPreview(string outputPath)
    {
        HasFileRegionOverlay = false;
        ResultImage = null;
        HasResultImage = false;
        _downloadablePreview = false;
        ResultPreviewTitle = "Output Preview";
        ResultPreviewPath = string.Empty;
        ResultPreviewPlaceholderText = ShellViewModel.IsPngPath(outputPath)
            ? "Generating image preview…"
            : "Current task generates files without visual image preview.";
    }

    internal bool ShowResultPreview(string outputPath)
    {
        HasFileRegionOverlay = false;
        if (!ShellViewModel.IsPngPath(outputPath))
        {
            return false;
        }

        try
        {
            using FileStream stream = new(outputPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            System.Windows.Media.Imaging.BitmapImage bitmap = new();
            bitmap.BeginInit();
            bitmap.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
            bitmap.StreamSource = stream;
            bitmap.EndInit();
            bitmap.Freeze();

            ResultImage = bitmap;
            HasResultImage = true;
            ResultPreviewPath = Path.GetFileName(outputPath);
            return true;
        }
        catch (Exception exception)
        {
            ResultPreviewPlaceholderText = "Image generated, but could not be loaded in UI.";
            _shell.AppendLog($"[{DateTime.Now:HH:mm:ss}] Preview load failed: {exception.Message}\n");
            return false;
        }
    }

    internal void OnWindowClosed()
    {
        _livePreviewTimer.Stop();
        _livePreviewCancellation?.Cancel();
        _livePreviewCancellation?.Dispose();
        TempFiles.TryDeleteTemporaryFile(_lastLivePreviewPath);
    }

    partial void OnHasResultImageChanged(bool value)
    {
        DownloadPreviewCommand.NotifyCanExecuteChanged();
        RaiseEmptyStateAction();
    }

    private bool CanDownloadPreview()
        => HasResultImage && _downloadablePreview && PreviewArtifactDirectory() is not null;

    /// <summary>
    /// Exports the live preview next to the picture it was derived from, named after that
    /// picture (<c>cd-track-preview.png</c>): the read-back picture belongs with the track or
    /// ISO the user keeps, and the projection belongs with its source picture, not in the
    /// preview's temporary directory.
    /// </summary>
    private void DownloadPreview()
    {
        string? previewPath = _lastLivePreviewPath;
        string? directory = PreviewArtifactDirectory();
        if (!_downloadablePreview || previewPath is null || directory is null || !File.Exists(previewPath))
        {
            return;
        }

        string artifactPath = PreviewInputPath();
        string baseName = Path.GetFileNameWithoutExtension(artifactPath);
        string destination = Path.Combine(
            directory,
            (baseName.Length == 0 ? "live-preview" : baseName) + "-preview.png");
        try
        {
            File.Copy(previewPath, destination, overwrite: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _shell.ShowToast("Export Failed", exception.Message, ToastKind.Error);
            _shell.AppendLog($"[{DateTime.Now:HH:mm:ss}] Preview export failed: {exception.Message}\n");
            return;
        }

        _shell.SetLastOutputPath(destination);
        _shell.ShowToast("Preview Exported", destination, ToastKind.Success);
        _shell.AppendLog($"[{DateTime.Now:HH:mm:ss}] Preview exported: {destination}\n");
    }

    /// <summary>The picture the preview is derived from: the artifact being read back, or the
    /// source picture being projected.</summary>
    private string PreviewInputPath()
    {
        return SelectedFamily switch
        {
            OpticalDiscFamily.Dvd => (IsCalibrationPreview ? _state.DvdImagePath : _state.DvdIsoPath).Trim(),
            OpticalDiscFamily.BluRay => (IsCalibrationPreview ? _state.BdImagePath : _state.BdIsoPath).Trim(),
            _ => (IsCalibrationPreview ? _state.CdImagePath : _state.CdTrackPath).Trim(),
        };
    }

    /// <summary>Folder holding that picture, or null when unknown.</summary>
    private string? PreviewArtifactDirectory()
    {
        string path = PreviewInputPath();
        if (path.Length == 0)
        {
            return null;
        }

        string? directory = Path.GetDirectoryName(path);
        return string.IsNullOrEmpty(directory) || !Directory.Exists(directory) ? null : directory;
    }
}
