using System.IO;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DiscImageStudio.BluRay;
using DiscImageStudio.Cd;
using DiscImageStudio.Core;
using DiscImageStudio.Dvd;
using DiscImageStudio.Services;

namespace DiscImageStudio.ViewModels;

/// <summary>The design page's tone module: the three tone parameters plus a disc projection
/// of the current composite through the same mapping the engines use. It lives next to the
/// composite pipeline because it is the last step of the same material: layers become a
/// picture, tone decides how that picture becomes pit/land bytes. Rendering waits for the
/// debounced composite and only runs while the design workspace is on screen; export, burn
/// and preview consume the shared <see cref="DiscParametersState"/> instead of rendering a
/// second copy.</summary>
public partial class ToneViewModel : ObservableObject
{
    private const int TonePreviewSize = 420;
    private const int TonePreviewBluRaySize = 512;
    private const int TonePreviewSamplesPerSector = 32;
    private const int TonePreviewDvdSamplesPerSector = 8;

    private readonly ShellViewModel _shell;
    private readonly DiscParametersState _state;
    private readonly DesignViewModel _design;

    private CancellationTokenSource? _tonePreviewCancellation;
    private int _tonePreviewRevision;
    private string? _compositePath;
    private string? _lastTonePreviewPath;
    private bool _tonePreviewReady;

    public ToneViewModel(ShellViewModel shell, DiscParametersState state, DesignViewModel design)
    {
        _shell = shell;
        _state = state;
        _design = design;
        ResetToneCommand = new RelayCommand(ResetTone, () => _shell.IsProjectOpen);
        RefreshTonePreviewCommand = new RelayCommand(
            RefreshTonePreviewNow,
            () => _shell.IsProjectOpen && _compositePath is not null);
        _shell.PropertyChanged += Shell_PropertyChanged;
        _state.PropertyChanged += State_PropertyChanged;
        _design.PropertyChanged += Design_PropertyChanged;
        _design.CompositeRendered += Design_CompositeRendered;
    }

    /// <summary>Exposed for the module's two-way bindings to the shared parameters.</summary>
    public DiscParametersState State => _state;

    /// <summary>The module mirrors the open project's family: CD shows the 2/4-level switch,
    /// DVD and BD fix the palette at two levels.</summary>
    public bool IsCd => _design.IsCd;

    public bool IsDvd => _design.IsDvd;

    public bool IsBluRay => _design.IsBluRay;

    [ObservableProperty]
    private BitmapSource? _tonePreviewImage;

    [ObservableProperty]
    private bool _hasTonePreview;

    [ObservableProperty]
    private string _tonePreviewStatus = "Preview is automatically generated after importing an image.";

    internal bool IsTonePreviewReady => _tonePreviewReady;

    public RelayCommand ResetToneCommand { get; }

    /// <summary>Redraws the tone preview without touching a parameter.</summary>
    public RelayCommand RefreshTonePreviewCommand { get; }

    private void Shell_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ShellViewModel.IsProjectOpen)
            or nameof(ShellViewModel.CurrentProject))
        {
            OnPropertyChanged(nameof(IsCd));
            OnPropertyChanged(nameof(IsDvd));
            OnPropertyChanged(nameof(IsBluRay));
            ResetToneCommand.NotifyCanExecuteChanged();
            RefreshTonePreviewCommand.NotifyCanExecuteChanged();
            // The composite path belongs to the previous project until the new design
            // finishes its debounced render; showing it would flash the old disc.
            ClearTonePreview();
            UpdateToneAvailability();
        }
        else if (e.PropertyName is nameof(ShellViewModel.CurrentWorkspace))
        {
            UpdateToneAvailability();
        }
    }

    private void Design_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is not (nameof(DesignViewModel.IsCd)
            or nameof(DesignViewModel.IsDvd)
            or nameof(DesignViewModel.IsBluRay)))
        {
            return;
        }

        OnPropertyChanged(nameof(IsCd));
        OnPropertyChanged(nameof(IsDvd));
        OnPropertyChanged(nameof(IsBluRay));
        ClearTonePreview();
    }

    private void State_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(DiscParametersState.ToneDitherStrength)
            or nameof(DiscParametersState.ToneLevels)
            or nameof(DiscParametersState.ToneBrightness)
            or nameof(DiscParametersState.ToneSrgbLinearization)
            or nameof(DiscParametersState.BdInvertMapping)
            or nameof(DiscParametersState.BdInnerRadius)
            or nameof(DiscParametersState.BdOuterRadius)
            or nameof(DiscParametersState.BdTotalSectors)
            or nameof(DiscParametersState.CdInnerRadius)
            or nameof(DiscParametersState.CdOuterRadius)
            or nameof(DiscParametersState.CdSectors)
            or nameof(DiscParametersState.CdDataDirectory)
            or nameof(DiscParametersState.CdVolumeLabel)
            or nameof(DiscParametersState.CdFilesAtOuterRing)
            or nameof(DiscParametersState.DvdInnerRadius)
            or nameof(DiscParametersState.DvdOuterRadius)
            or nameof(DiscParametersState.DvdTotalSectors)
            or nameof(DiscParametersState.DvdPitchLinear)
            or nameof(DiscParametersState.DvdPitchQuadratic)
            or nameof(DiscParametersState.DvdPitchCubic))
        {
            ScheduleTonePreview();
        }
    }

    private void Design_CompositeRendered(string? compositePath)
    {
        _compositePath = compositePath;
        RefreshTonePreviewCommand.NotifyCanExecuteChanged();
        if (compositePath is null)
        {
            ClearTonePreview();
            return;
        }

        ScheduleTonePreview();
    }

    /// <summary>Only renders while the design page is the surface on screen: a slider edit
    /// elsewhere does not queue disc-sized work nobody is looking at, and returning to the
    /// page catches up through this same path.</summary>
    private void UpdateToneAvailability()
    {
        if (!_shell.IsProjectOpen)
        {
            ClearTonePreview();
            return;
        }

        if (_shell.CurrentWorkspace == Workspace.Design)
        {
            ScheduleTonePreview();
        }
    }

    private void ScheduleTonePreview()
    {
        if (!_shell.IsProjectOpen
            || _shell.CurrentWorkspace != Workspace.Design
            || _compositePath is null)
        {
            return;
        }

        _tonePreviewCancellation?.Cancel();
        _tonePreviewCancellation?.Dispose();
        CancellationTokenSource cancellation = new();
        _tonePreviewCancellation = cancellation;
        int revision = ++_tonePreviewRevision;
        _ = RenderTonePreviewAsync(revision, cancellation.Token);
    }

    private void RefreshTonePreviewNow()
    {
        if (_compositePath is null)
        {
            return;
        }

        _tonePreviewCancellation?.Cancel();
        _tonePreviewCancellation?.Dispose();
        CancellationTokenSource cancellation = new();
        _tonePreviewCancellation = cancellation;
        int revision = ++_tonePreviewRevision;
        _ = RenderTonePreviewAsync(revision, cancellation.Token);
    }

    private async Task RenderTonePreviewAsync(int revision, CancellationToken cancellationToken)
    {
        string? sourcePath = _compositePath;
        if (sourcePath is null)
        {
            return;
        }

        string outputPath = Path.Combine(
            TempFiles.LivePreviewDirectory,
            $"design-tone-{revision}.png");
        bool keepOutput = false;
        try
        {
            await Task.Delay(180, cancellationToken);
            Directory.CreateDirectory(TempFiles.LivePreviewDirectory);
            TonePreviewStatus = "Rendering with current tone settings…";

            int samplesPerSector;
            if (IsBluRay)
            {
                BluRayDiscParameters parameters = ReadBluRayPreviewParameters();
                bool invert = _state.BdInvertMapping;
                ToneProfile tone = _state.BluRayTone;
                samplesPerSector = 0;
                await StaWorker.RunAsync(() =>
                {
                    BluRayPreviewRenderer.RenderProjection(
                        sourcePath,
                        outputPath,
                        parameters,
                        TonePreviewBluRaySize,
                        invert,
                        tone,
                        cancellationToken);
                    return 0;
                });
            }
            else if (IsDvd)
            {
                DvdStreamingOptions options = ReadDvdPreviewOptions();
                samplesPerSector = TonePreviewDvdSamplesPerSector;
                await StaWorker.RunAsync(() =>
                {
                    DvdTonePreview.Render(
                        sourcePath,
                        outputPath,
                        options,
                        TonePreviewSize,
                        samplesPerSector,
                        cancellationToken);
                    return 0;
                });
            }
            else
            {
                CdDiscParameters parameters = ReadCdPreviewParameters();
                ToneProfile tone = _state.Tone;
                samplesPerSector = TonePreviewSamplesPerSector;
                string dataDirectory = _state.CdDataDirectory.Trim();
                string volumeLabel = _state.CdVolumeLabel.Trim();
                bool filesAtOuterRing = _state.CdFilesAtOuterRing;
                await StaWorker.RunAsync(() =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    CdDataLayoutPlan? plan = dataDirectory.Length > 0
                        ? CdDataImageGenerator.PrepareLayout(dataDirectory, parameters, volumeLabel, filesAtOuterRing)
                        : null;
                    cancellationToken.ThrowIfCancellationRequested();
                    if (plan is not null)
                    {
                        CdDataImageGenerator.PreviewWarp(sourcePath, outputPath, parameters, parameters,
                            TonePreviewSize, samplesPerSector, tone, plan, cancellationToken);
                        FileRegionPreviewOverlay.ApplyCd(outputPath, parameters,
                            plan.DrawingStartLba, plan.DrawingEndLbaExclusive, cancellationToken);
                    }
                    else
                    {
                        CdTrackGenerator.PreviewWarp(sourcePath, outputPath, parameters, parameters,
                            TonePreviewSize, samplesPerSector, tone, cancellationToken);
                    }
                    return 0;
                });
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (revision != _tonePreviewRevision)
            {
                return;
            }

            BitmapImage bitmap = new();
            using (FileStream stream = new(
                outputPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite))
            {
                bitmap.BeginInit();
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.StreamSource = stream;
                bitmap.EndInit();
                bitmap.Freeze();
            }

            string? previousPath = _lastTonePreviewPath;
            _lastTonePreviewPath = outputPath;
            keepOutput = true;
            TonePreviewImage = bitmap;
            HasTonePreview = true;
            _tonePreviewReady = true;
            TonePreviewStatus = "Real-time update";
            TempFiles.TryDeleteTemporaryFile(previousPath);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            TonePreviewStatus = $"Tone preview failed: {exception.Message}";
        }
        finally
        {
            if (!keepOutput)
            {
                TempFiles.TryDeleteTemporaryFile(outputPath);
            }
        }
    }

    private void ClearTonePreview()
    {
        _tonePreviewCancellation?.Cancel();
        _tonePreviewCancellation?.Dispose();
        _tonePreviewCancellation = null;
        _compositePath = null;
        RefreshTonePreviewCommand.NotifyCanExecuteChanged();
        TonePreviewImage = null;
        HasTonePreview = false;
        _tonePreviewReady = false;
        TonePreviewStatus = "Preview is automatically generated after importing an image.";
        TempFiles.TryDeleteTemporaryFile(_lastTonePreviewPath);
        _lastTonePreviewPath = null;
    }

    private void ResetTone()
    {
        _state.ToneDitherStrength = ToneProfile.Photo.DitherStrength;
        _state.ToneLevels = ToneProfile.Photo.Levels;
        _state.ToneBrightness = ToneProfile.Photo.Brightness;
        _state.ToneSrgbLinearization = ToneProfile.Photo.SrgbLinearization;
    }

    private BluRayDiscParameters ReadBluRayPreviewParameters()
    {
        long totalSectors = ParameterParser.ParsePositiveLong(_state.BdTotalSectors, "Blu-ray Total Sectors");
        if (totalSectors > uint.MaxValue)
        {
            throw new ArgumentOutOfRangeException("Blu-ray Total Sectors", "Total sectors exceeds the Blu-ray engine supported range.");
        }

        BluRayDiscParameters parameters = new(
            checked((uint)totalSectors),
            1,
            ParameterParser.ParsePositiveDouble(_state.BdInnerRadius, "Blu-ray Inner Radius"),
            ParameterParser.ParsePositiveDouble(_state.BdOuterRadius, "Blu-ray Outer Radius"));
        parameters.Validate();
        return parameters;
    }

    private CdDiscParameters ReadCdPreviewParameters()
    {
        CdDiscParameters parameters = new(
            ParameterParser.ParsePositiveDouble(_state.CdInnerRadius, "Inner Radius"),
            ParameterParser.ParsePositiveDouble(_state.CdOuterRadius, "Outer Radius"),
            ParameterParser.ParsePositiveLong(_state.CdSectors, "Sectors"));
        parameters.Validate();
        return parameters;
    }

    private DvdStreamingOptions ReadDvdPreviewOptions()
    {
        long totalSectors = ParameterParser.ParsePositiveLong(_state.DvdTotalSectors, "Total Sectors");
        if (totalSectors > uint.MaxValue)
        {
            throw new ArgumentOutOfRangeException("Total Sectors", "Total sectors exceeds the DVD engine supported range.");
        }

        DvdStreamingOptions options = new(
            checked((uint)totalSectors),
            ParameterParser.ParsePositiveDouble(_state.DvdInnerRadius, "Inner Radius"),
            ParameterParser.ParsePositiveDouble(_state.DvdOuterRadius, "Outer Radius"),
            PitchLinear: ParameterParser.ParseDouble(_state.DvdPitchLinear, "DVD Pitch Linear Term"),
            PitchQuadratic: ParameterParser.ParseDouble(_state.DvdPitchQuadratic, "DVD Pitch Quadratic Term"),
            PitchCubic: ParameterParser.ParseDouble(_state.DvdPitchCubic, "DVD Pitch Cubic Term"),
            Tone: _state.DvdTone);
        options.Validate();
        return options;
    }
}
