using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DiscImageStudio.BluRay;
using DiscImageStudio.Cd;
using DiscImageStudio.Controls;
using DiscImageStudio.Core;
using DiscImageStudio.Core.Calibration;
using DiscImageStudio.Dvd;
using DiscImageStudio.Services;

namespace DiscImageStudio.ViewModels;

public partial class CalibrationViewModel : ObservableObject
{
    private readonly ShellViewModel _shell;
    private readonly DiscParametersState _state;
    private CalibrationTarget? _target;
    private CalibrationParameters? _searchCenter;
    private CalibrationFitResult? _fit;
    private CancellationTokenSource? _cancellation;
    private CancellationTokenSource? _previewCancellation;
    private bool _cdInterleave = true;
    private string? _sessionPath;
    private bool _installing;
    private bool _previewReady;
    private long _inputRevision;
    private bool _isSolving;
    private CalibrationFitOptions _loadedFitOptions = new();

    public CalibrationViewModel(ShellViewModel shell, DiscParametersState state)
    {
        _shell = shell;
        _state = state;
        CreateTargetCommand = new RelayCommand(CreateTarget, () => !IsBusy);
        BrowseOutputCommand = new RelayCommand(BrowseOutput, () => !IsBusy);
        ExportPngCommand = new AsyncRelayCommand(ExportPngAsync, CanUseTarget);
        GenerateImageCommand = new AsyncRelayCommand(GenerateImageAsync, CanUseTarget);
        StreamBurnCommand = new RelayCommand(PrepareStreamBurn, CanUseTarget);
        SaveSessionCommand = new RelayCommand(SaveSession, CanUseTarget);
        OpenSessionCommand = new AsyncRelayCommand(OpenSessionAsync, () => !IsBusy);
        SolveCommand = new AsyncRelayCommand(SolveAsync, CanSolveTrace);
        CancelCommand = new RelayCommand(() => _cancellation?.Cancel(), () => _cancellation is not null);
        ApplyParametersCommand = new RelayCommand(ApplyParameters, CanApplyParameters);
        InitializePhotoCommands();
        shell.BusyChanged += () => { OnPropertyChanged(nameof(IsBusy)); RefreshCommands(); };
        // The parameter read-out under Create Target from Current Parameters follows the shared parameter store:
        // presets, project edits and applied calibration results all re-render it.
        state.PropertyChanged += (_, _) => OnPropertyChanged(nameof(CurrentParametersSummary));
    }

    [ObservableProperty] private string _selectedDiscType = "CD";
    [ObservableProperty] private BitmapSource? _targetImage;
    [ObservableProperty] private double _canvasOuterRadiusMm = 60;
    [ObservableProperty] private IReadOnlyList<CoarseCandidate> _coarseCandidates = [];
    [ObservableProperty] private CoarseCandidate? _selectedCoarseCandidate;
    // The range is a search box for the two radius parameters, not a curve-shape choice.
    // Auto starts at the tight box and widens only when the optimum touches its edge, so
    // healthy discs are not silently clamped to a user-picked boundary.
    public IReadOnlyList<CoarseSearchRange> CoarseSearchRangeChoices { get; } =
        [new("Auto (Recommended)", 0.02, Auto: true), new("±0.02 mm", 0.02),
         new("±0.1 mm", 0.1), new("±0.5 mm", 0.5)];
    [ObservableProperty] private CoarseSearchRange _selectedCoarseSearchRange = new("Auto (Recommended)", 0.02, Auto: true);
    public string SearchRangeHeader => SelectedCoarseSearchRange.Auto
        ? "Search Range (Auto)"
        : $"Search Range ({SelectedCoarseSearchRange.Label})";
    private double _fitRangeMm = 0.02;
    private bool _fitRangeExpanded;
    [ObservableProperty] private bool _contourConfirmed;
    [ObservableProperty] private bool _fitDvdPitch = true;
    public bool IsDvdTarget => _target?.Parameters.Kind == CalibrationDiscKind.Dvd;

    public bool IsBluRayTarget => _target?.Parameters.Kind == CalibrationDiscKind.BluRay;
    [ObservableProperty] private string _outputPath = string.Empty;
    // Page-bottom status is gone; the surviving consumers are the log page (RunAsync logs
    // the result) and the toasts (errors). This field feeds the log only.
    private string _statusMessage = string.Empty;

    /// <summary>Last operation message, kept for diagnostics (tests, future consumers);
    /// the log page carries the same lines.</summary>
    public string LastStatusMessage => _statusMessage;
    [ObservableProperty] private string _fitAssessment = "All valid points participate in computation; verify result against photo curve.";
    public bool IsBusy => _shell.IsBusy;
    public CalibrationTarget? Target => _target;
    public bool CdInterleave => _cdInterleave;

    /// <summary>The generation geometry a new target is built from: the project's current
    /// inner/outer radii and, for DVD, the cubic pitch term. Shown under the button so the
    /// inputs stay visible without a disc-type selector.</summary>
    public string CurrentParametersSummary => SelectedDiscType switch
    {
        "DVD" => $"Inner radius {Trimmed(_state.DvdInnerRadius)} mm · Outer radius {Trimmed(_state.DvdOuterRadius)} mm · Cubic term {Trimmed(_state.DvdPitchCubic)}",
        "BD" => $"Inner radius {Trimmed(_state.BdInnerRadius)} mm · Outer radius {Trimmed(_state.BdOuterRadius)} mm · "
            + $"{Trimmed(_state.BdTotalSectors)} sectors",
        _ => $"Inner radius {Trimmed(_state.CdInnerRadius)} mm · Outer radius {Trimmed(_state.CdOuterRadius)} mm",
    };

    private static string Trimmed(string value) =>
        string.IsNullOrWhiteSpace(value) ? "—" : value.Trim();

    // ---- Step column: state chips and progressive disclosure ----
    // One step is active at a time and its card is the expanded one; finished steps collapse
    // into a check-marked header. The photo/trace transitions raise these from the photo
    // partial, target installation raises them in InstallTarget.

    public StepCardState StepOneState => _target is null ? StepCardState.Active : StepCardState.Done;

    public StepCardState StepTwoState => _target is null
        ? StepCardState.Pending
        : IsPhotoStep ? StepCardState.Active : StepCardState.Done;

    public StepCardState StepThreeState => IsTraceStep ? StepCardState.Active : StepCardState.Pending;

    [ObservableProperty] private bool _stepOneExpanded = true;
    [ObservableProperty] private bool _stepTwoExpanded;
    [ObservableProperty] private bool _stepThreeExpanded;

    // Accordion: opening one step closes the others, so the expanded card can claim the full
    // remaining height of the step column and all three headers stay on screen. Collapse is
    // still allowed, which leaves all three closed.
    partial void OnStepOneExpandedChanged(bool value) { if (value) CollapseOtherSteps(1); }
    partial void OnStepTwoExpandedChanged(bool value) { if (value) CollapseOtherSteps(2); }
    partial void OnStepThreeExpandedChanged(bool value) { if (value) CollapseOtherSteps(3); }

    private void CollapseOtherSteps(int keep)
    {
        if (keep != 1 && StepOneExpanded) StepOneExpanded = false;
        if (keep != 2 && StepTwoExpanded) StepTwoExpanded = false;
        if (keep != 3 && StepThreeExpanded) StepThreeExpanded = false;
    }

    // Canvas view toggles: they change what the stage draws and nothing else, so they live
    // here instead of view code-behind (which used to reset them by reach-in).
    [ObservableProperty] private bool _showTargetPreview;
    [ObservableProperty] private bool _showPrediction = true;

    public RelayCommand CreateTargetCommand { get; }
    public RelayCommand BrowseOutputCommand { get; }
    public IAsyncRelayCommand ExportPngCommand { get; }
    public IAsyncRelayCommand GenerateImageCommand { get; }
    public RelayCommand StreamBurnCommand { get; }
    public RelayCommand SaveSessionCommand { get; }
    public IAsyncRelayCommand OpenSessionCommand { get; }
    public IAsyncRelayCommand SolveCommand { get; }
    public RelayCommand CancelCommand { get; }
    public RelayCommand ApplyParametersCommand { get; }

    internal void EnsureTarget()
    {
        if (_target is not null)
        {
            return;
        }

        // The first target follows the project's disc family; the page switch can still be
        // used afterwards to build a target for the other family on purpose.
        SelectedDiscType = _shell.CurrentProject?.Family switch
        {
            OpticalDiscFamily.Dvd => "DVD",
            OpticalDiscFamily.BluRay => "BD",
            _ => "CD",
        };
        CreateTarget();
    }
    /// <summary>Opening another project invalidates a target built from the previous one;
    /// the next visit to the calibration page builds a target from the new geometry.</summary>
    internal void ClearTargetForProjectChange()
    {
        _cancellation?.Cancel();
        _previewCancellation?.Cancel();
        _target = null;
        _sessionPath = null;
        OutputPath = string.Empty;
        TargetImage = null;
        ResetPhoto();
        InvalidateFit();
        OnPropertyChanged(nameof(Target));
        OnPropertyChanged(nameof(IsDvdTarget));
        OnPropertyChanged(nameof(IsBluRayTarget));
        OnPropertyChanged(nameof(StepOneState));
        OnPropertyChanged(nameof(StepTwoState));
        OnPropertyChanged(nameof(StepThreeState));
        RefreshCommands();
        _shell.Burn?.OnCalibrationTargetChanged();
    }

    internal void OnWindowClosed() { _cancellation?.Cancel(); _previewCancellation?.Cancel(); }
    private bool CanUseTarget() => !IsBusy && _target is not null;
    private bool CanApplyParameters() => !IsBusy && IsTraceStep && _photoTransform is not null && _fit?.Succeeded == true
        && SelectedCoarseCandidate is { } selected && ContourConfirmed && _previewReady
        && CandidateResidualAccepted(selected)
        && CoarseCandidates.Contains(selected)
        && _fit.Candidates.Any(c => c.Parameters == selected.Parameters && c.RotationRadians == selected.RotationRadians);
    partial void OnContourConfirmedChanged(bool value) => ApplyParametersCommand?.NotifyCanExecuteChanged();

    // A visible gap is not a number anyone can argue with: 0.35 mm mean is already a
    // quarter of a millimetre away from the predicted curve at every point. The max bound
    // catches a single grossly misplaced click even when the mean stays comfortable.
    private static bool CandidateResidualAccepted(CoarseCandidate candidate) =>
        double.IsFinite(candidate.RmsErrorMm) && double.IsFinite(candidate.MaxErrorMm)
        && candidate.RmsErrorMm <= 0.35 && candidate.MaxErrorMm <= 0.8;

    partial void OnSelectedDiscTypeChanged(string value)
    {
        OnPropertyChanged(nameof(CurrentParametersSummary));
    }

    private void CreateTarget()
    {
        try
        {
            CalibrationParameters p = SelectedDiscType switch
            {
                "DVD" => new(CalibrationDiscKind.Dvd, Number(_state.DvdInnerRadius), Number(_state.DvdOuterRadius),
                    ParameterParser.ParsePositiveLong(_state.DvdTotalSectors, "DVD Sectors"),
                    DvdStreamingOptions.StandardChannelBitLengthNm,
                    ParameterParser.ParseDouble(_state.DvdPitchLinear, "DVD Linear Term"),
                    ParameterParser.ParseDouble(_state.DvdPitchQuadratic, "DVD Quadratic Term"),
                    ParameterParser.ParseDouble(_state.DvdPitchCubic, "DVD Cubic Term")),
                "BD" => CreateBluRayParameters(),
                _ => new(CalibrationDiscKind.Cd, Number(_state.CdInnerRadius), Number(_state.CdOuterRadius),
                    ParameterParser.ParsePositiveLong(_state.CdSectors, "CD Sectors"),
                    CdDiscParameters.StandardLinearVelocityMmPerSecond),
            };
            ValidateOutputParameters(p);
            InstallTarget(CalibrationTarget.Create(p), new(), _state.CdInterleave);
            _sessionPath = null;
            OutputPath = string.Empty;
        }
        catch (Exception ex) { Report(ex); }
    }

    private static double Number(string raw) => ParameterParser.ParsePositiveDouble(raw, "Calibration Parameters");

    private CalibrationParameters CreateBluRayParameters()
    {
        long sectors = ParameterParser.ParsePositiveLong(_state.BdTotalSectors, "Blu-ray Total Sectors");
        if (sectors > uint.MaxValue)
        {
            throw new ArgumentOutOfRangeException("Blu-ray Total Sectors", "Total sectors exceeds Blu-ray engine supported range.");
        }

        uint totalSectors = checked((uint)sectors);
        BluRayDiscParameters disc = new(
            totalSectors, 1, Number(_state.BdInnerRadius), Number(_state.BdOuterRadius));
        disc.Validate();
        return new CalibrationParameters(
            CalibrationDiscKind.BluRay,
            disc.InnerRadiusMm,
            disc.OuterRadiusMm,
            disc.TotalSectors,
            BluRayDiscParameters.ChannelBitLengthNm,
            BluRayInvertMapping: _state.BdInvertMapping,
            DrawingStartSector: 0,
            DrawingEndSectorExclusive: disc.TotalSectors);
    }

    private void InstallTarget(CalibrationTarget target, CalibrationFitOptions? options, bool interleave)
    {
        _installing = true;
        try
        {
            _previewCancellation?.Cancel();
            ResetPhoto();
            _target = target;
            _cdInterleave = interleave;
            _searchCenter = options?.SearchCenter ?? target.Parameters;
            _loadedFitOptions = options ?? new();
            FitDvdPitch = _loadedFitOptions.FitDvdPitch;
            double savedRange = options?.RadiusRangeMm ?? 0.02;
            // Sessions saved before the auto mode carry no flag (null) and default to auto;
            // a session that explicitly saved a manual box keeps that box.
            bool autoRange = options?.AutoRange ?? true;
            SelectedCoarseSearchRange = autoRange
                ? CoarseSearchRangeChoices[0]
                : CoarseSearchRangeChoices.FirstOrDefault(r => !r.Auto && r.RadiusRangeMm == savedRange)
                    ?? new($"{savedRange:0.###} mm", savedRange);
            SelectedDiscType = target.Parameters.Kind switch
            {
                CalibrationDiscKind.Dvd => "DVD",
                CalibrationDiscKind.BluRay => "BD",
                _ => "CD",
            };
            // The 60 mm canvas is a disc-preview background; the target itself maps its
            // reference pixels using the saved generation outer radius.
            CanvasOuterRadiusMm = Math.Max(CalibrationReferencePattern.CanvasRadiusMm, target.Parameters.OuterRadiusMm);
            TargetImage = CalibrationPatternRenderer.Render(target, CanvasOuterRadiusMm, 900);
            InvalidateFit();
            OnPropertyChanged(nameof(Target));
            OnPropertyChanged(nameof(IsDvdTarget));
            OnPropertyChanged(nameof(IsBluRayTarget));
            OnPropertyChanged(nameof(StepOneState));
            OnPropertyChanged(nameof(StepTwoState));
            // The target exists, so stage 1 is done and photographing it takes over.
            StepOneExpanded = false;
            StepTwoExpanded = true;
            RefreshCommands();
            _shell.Burn?.OnCalibrationTargetChanged();
        }
        finally { _installing = false; }
    }

    private void InvalidateFit()
    {
        _inputRevision++;
        if (_isSolving) _cancellation?.Cancel();
        _fit = null;
        ContourConfirmed = false;
        _previewCancellation?.Cancel();
        _previewReady = false;
        CrossPredictionContours = [];
        OutlierTracePointIndices = [];
        CoarseCandidates = [];
        SelectedCoarseCandidate = null;
        _fitRangeExpanded = false;
        FitAssessment = "All valid points participate in computation; verify result against photo curve.";
        ApplyParametersCommand.NotifyCanExecuteChanged();
    }

    private CalibrationFitOptions FitOptions() => _loadedFitOptions with
    {
        SearchCenter = _searchCenter,
        RadiusRangeMm = SelectedCoarseSearchRange.RadiusRangeMm,
        FitDvdPitch = FitDvdPitch,
        AdaptiveRobustScale = true,
        AutoRange = SelectedCoarseSearchRange.Auto,
    };

    partial void OnFitDvdPitchChanged(bool value)
    {
        if (!_installing) InvalidateFit();
    }
    private CalibrationSession Session() => new(_target ?? throw new InvalidOperationException("Please create pattern first."),
        [], FitOptions(), _cdInterleave, PhotoSession());

    private async Task SolveAsync()
    {
        if (_target is not { } target) return;
        InvalidateFit();
        long revision = _inputRevision;
        var traces = CrossTraces();
        var options = FitOptions();
        int pointCount = traces.Sum(trace => trace.Points.Count);
        await RunAsync($"Computing parameters with all {pointCount} curve points…", async token =>
        {
            _isSolving = true;
            FitAttempt attempt = await Task.Run(() => FitWithAutoRange(
                target, traces, options, SelectedCoarseSearchRange.Auto, token), token);
            CalibrationFitResult result = attempt.Result;
            _fitRangeMm = attempt.RangeMm;
            _fitRangeExpanded = attempt.Expanded;
            var candidates = result.Candidates.Select((c, index) =>
                new CoarseCandidate($"Candidate {index + 1} · Deviation {c.RmsErrorMm:0.###} mm", c.Parameters,
                    c.RotationRadians, c.RmsErrorMm, c.MaxErrorMm, c.OutlierCount)).ToArray();
            token.ThrowIfCancellationRequested();
            if (revision != _inputRevision || !ReferenceEquals(target, _target)) throw new OperationCanceledException();
            // Publish the result and its candidate set together, only for the current inputs.
            _fit = result;
            ContourConfirmed = false;
            CoarseCandidates = candidates;
            _installing = true;
            try { SelectedCoarseCandidate = CoarseCandidates.FirstOrDefault(); }
            finally { _installing = false; }
            await ShowCandidateAsync(SelectedCoarseCandidate);
            token.ThrowIfCancellationRequested();
            UpdateFitAssessment();
            if (!result.Succeeded)
                _statusMessage = $"Computation did not converge, all {pointCount} points retained.\n{string.Join("\n", result.Warnings)}";
            else if (SelectedCoarseCandidate is { } selected && !CandidateResidualAccepted(selected))
                _statusMessage = $"Large deviation between candidates and photo points; all {pointCount} points retained. Please adjust red points or add curve points.";
            else if (_previewReady)
                _statusMessage = $"Generated parameter candidates using all {pointCount} points{(attempt.Expanded ? $", search range expanded to ±{attempt.RangeMm:0.###} mm" : string.Empty)}. Check that orange prediction aligns with cyan photo points before applying.";
        });
    }

    /// <summary>
    /// Auto mode widens the radius box one step at a time until the optimum is interior.
    /// A boundary-clamped answer is not a measurement, so it is never published as the
    /// final result when a wider box can contain the real optimum. Explicit ranges keep
    /// their exact meaning for users who want to constrain the search.
    /// </summary>
    private static FitAttempt FitWithAutoRange(CalibrationTarget target, IReadOnlyList<CalibrationCrossTrace> traces,
        CalibrationFitOptions options, bool auto, CancellationToken token)
    {
        if (!auto)
            return new(CalibrationCrossFitter.Fit(target, traces, options, token), options.RadiusRangeMm, false);

        double[] ranges = [options.RadiusRangeMm, 0.1, 0.5, 2, 5];
        CalibrationFitResult? best = null;
        CalibrationFitResult? last = null;
        double bestRange = options.RadiusRangeMm;
        foreach (double range in ranges.Where(range => range >= options.RadiusRangeMm)
                     .Distinct().OrderBy(range => range))
        {
            token.ThrowIfCancellationRequested();
            CalibrationFitResult result = CalibrationCrossFitter.Fit(target, traces,
                options with { RadiusRangeMm = range }, token);
            last = result;
            // BD's observed curve continues beyond the drawing band, so a narrow box can
            // return an interior solution with a visibly unusable residual instead of a
            // boundary-clamped one. Keep widening while the fit is that poor.
            bool unusableBluRay = target.Parameters.Kind == CalibrationDiscKind.BluRay
                && result.RmsErrorMm > 0.8;
            if (result.Succeeded && !result.BoundaryLimited && !unusableBluRay)
                return new(result, range, range > options.RadiusRangeMm);
            if (result.Succeeded && (best is null || result.RmsErrorMm < best.RmsErrorMm))
            {
                best = result;
                bestRange = range;
            }
            if (!result.Succeeded && IsInputFailure(result)) break;
        }

        CalibrationFitResult fallback = best ?? last
            ?? CalibrationCrossFitter.Fit(target, traces, options, token);
        return new(fallback, bestRange, bestRange > options.RadiusRangeMm);
    }

    private static bool IsInputFailure(CalibrationFitResult result) => !result.Succeeded
        && result.Warnings.Any(warning =>
            warning.Contains("at least", StringComparison.Ordinal)
            || warning.Contains("too narrow", StringComparison.Ordinal)
            || warning.Contains("falls inward", StringComparison.Ordinal)
            || warning.Contains("half a turn", StringComparison.Ordinal)
            || warning.Contains("too large", StringComparison.Ordinal));

    private void UpdateFitAssessment()
    {
        if (_fit is null)
        {
            FitAssessment = "All valid points participate in computation; verify result against photo curve.";
            return;
        }
        if (!_fit.Succeeded)
        {
            FitAssessment = _fit.Warnings.FirstOrDefault() ?? "Computation did not converge; all points retained.";
            return;
        }
        if (SelectedCoarseCandidate is not { } candidate)
        {
            FitAssessment = "No applicable candidates.";
            return;
        }

        bool accepted = CandidateResidualAccepted(candidate);
        string range = _fitRangeExpanded
            ? $"\nSearch automatically expanded to ±{_fitRangeMm:0.###} mm。"
            : string.Empty;
        if (_fit.BoundaryLimited)
            range += "\nOptimal result is near search boundary; add points or check placement.";
        string outliers = OutlierTracePointIndices.Count > 0
            ? $"\nMarked {OutlierTracePointIndices.Count} points with large deviation."
            : string.Empty;
        FitAssessment = accepted
            ? $"Candidate · Mean dev {candidate.RmsErrorMm:0.###} mm · Max {candidate.MaxErrorMm:0.###} mm, awaiting verification{range}{outliers}"
            : $"Failed · Mean dev {candidate.RmsErrorMm:0.###} mm · Max {candidate.MaxErrorMm:0.###} mm, cannot apply{range}{outliers}";
    }

    private void ApplyParameters()
    {
        if (!CanApplyParameters() || SelectedCoarseCandidate?.Parameters is not { } p) return;
        string ri = p.InnerRadiusMm.ToString("G17", CultureInfo.InvariantCulture);
        string ro = p.OuterRadiusMm.ToString("G17", CultureInfo.InvariantCulture);
        switch (p.Kind)
        {
            case CalibrationDiscKind.Cd:
                _state.CdInnerRadius = ri; _state.CdOuterRadius = ro;
                _state.CdActualInnerRadius = ri; _state.CdActualOuterRadius = ro;
                _state.CdSectors = p.Sectors.ToString(CultureInfo.InvariantCulture);
                break;
            case CalibrationDiscKind.BluRay:
                _state.BdInnerRadius = ri; _state.BdOuterRadius = ro;
                _state.BdActualInnerRadius = ri; _state.BdActualOuterRadius = ro;
                _state.BdTotalSectors = p.Sectors.ToString(CultureInfo.InvariantCulture);
                _state.BdInvertMapping = p.BluRayInvertMapping;
                break;
            default:
                _state.DvdInnerRadius = ri; _state.DvdOuterRadius = ro;
                _state.DvdActualInnerRadius = ri; _state.DvdActualOuterRadius = ro;
                _state.DvdPitchLinear = _state.DvdActualPitchLinear = p.PitchLinear.ToString("G17", CultureInfo.InvariantCulture);
                _state.DvdPitchQuadratic = _state.DvdActualPitchQuadratic = p.PitchQuadratic.ToString("G17", CultureInfo.InvariantCulture);
                _state.DvdPitchCubic = _state.DvdActualPitchCubic = p.PitchCubic.ToString("G17", CultureInfo.InvariantCulture);
                _state.DvdTotalSectors = p.Sectors.ToString(CultureInfo.InvariantCulture);
                break;
        }

        string family = p.Kind switch
        {
            CalibrationDiscKind.Cd => "CD",
            CalibrationDiscKind.BluRay => "BD",
            _ => "DVD",
        };
        _statusMessage = $"Applied to {family} parameters. Original calibration record retained for further verification.";
        _shell.AppendLog($"[{DateTime.Now:HH:mm:ss}] Calibration {Target?.Id}: {_statusMessage} InnerRadius={ri}, OuterRadius={ro}\n");
        _shell.NotifyCalibrationParametersApplied();
    }

    partial void OnSelectedCoarseCandidateChanged(CoarseCandidate? value)
    {
        ContourConfirmed = false;
        _previewReady = false;
        UpdateFitAssessment();
        if (!_installing) _ = ShowCandidateAsync(value);
        ApplyParametersCommand?.NotifyCanExecuteChanged();
    }

    partial void OnSelectedCoarseSearchRangeChanged(CoarseSearchRange value)
    {
        OnPropertyChanged(nameof(SearchRangeHeader));
        if (!_installing) InvalidateFit();
    }

    private async Task ShowCandidateAsync(CoarseCandidate? candidate)
    {
        _previewCancellation?.Cancel();
        _previewReady = false;
        CrossPredictionContours = [];
        OutlierTracePointIndices = [];
        ApplyParametersCommand.NotifyCanExecuteChanged();
        if (candidate is null || _target is not { } target) return;
        var traceSnapshot = TracePoints;
        double[] observedRadii = traceSnapshot.Select(point => Math.Sqrt(point.XMm * point.XMm + point.YMm * point.YMm)).ToArray();
        if (observedRadii.Length < 2) return;
        double minimumObservedRadius = observedRadii.Min(), maximumObservedRadius = observedRadii.Max();
        if (!(maximumObservedRadius > minimumObservedRadius)) return;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_cancellation?.Token ?? CancellationToken.None);
        _previewCancellation = cancellation;
        long revision = _inputRevision;
        try
        {
            var preview = await Task.Run(() =>
            {
                cancellation.Token.ThrowIfCancellationRequested();
                var prediction = CalibrationCrossPrediction.GetOutline(target, candidate.Parameters,
                    minimumObservedRadius, maximumObservedRadius, candidate.RotationRadians, cancellationToken: cancellation.Token);
                // The predicted curve is the reference; a point beyond a small multiple of
                // the mean residual is visibly off it, not measurement noise.
                double threshold = Math.Max(0.2, candidate.RmsErrorMm * 2.5);
                int[] outliers = traceSnapshot
                    .Where(point => DistanceToPolyline(point, prediction) > threshold)
                    .Select(point => point.Index).OrderBy(index => index).ToArray();
                return (Contour: prediction.Select(point => new Point(point.X, point.Y)).ToArray(), Outliers: outliers);
            }, cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested();
            if (revision != _inputRevision || !ReferenceEquals(_target, target)
                || !ReferenceEquals(SelectedCoarseCandidate, candidate)) return;
            CrossPredictionContours = [preview.Contour];
            OutlierTracePointIndices = preview.Outliers;
            _previewReady = true;
            UpdateFitAssessment();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (!cancellation.IsCancellationRequested)
            {
                CrossPredictionContours = [];
                ContourConfirmed = false;
                Report(ex);
            }
        }
        finally
        {
            if (ReferenceEquals(_previewCancellation, cancellation)) _previewCancellation = null;
            ApplyParametersCommand.NotifyCanExecuteChanged();
        }
    }

    private static double DistanceToPolyline(CalibrationTracePoint point, IReadOnlyList<CalibrationPoint> line)
    {
        double best = double.PositiveInfinity;
        for (int i = 1; i < line.Count; i++)
        {
            double ax = line[i - 1].X, ay = line[i - 1].Y;
            double dx = line[i].X - ax, dy = line[i].Y - ay;
            double t = Math.Clamp(((point.XMm - ax) * dx + (point.YMm - ay) * dy)
                / Math.Max(1e-30, dx * dx + dy * dy), 0, 1);
            double ex = point.XMm - ax - t * dx, ey = point.YMm - ay - t * dy;
            best = Math.Min(best, ex * ex + ey * ey);
        }
        return Math.Sqrt(best);
    }

    private void BrowseOutput()
    {
        if (_target is null) return;
        bool discImage = _target.Parameters.Kind is CalibrationDiscKind.Dvd or CalibrationDiscKind.BluRay;
        string? path = _shell.Dialogs?.PickSave(
            discImage ? "Disc Image|*.iso" : "CD Audio Track|*.wav|CD Raw Audio Track|*.raw",
            discImage ? ".iso" : ".wav",
            $"calibration-{_target.Id[..8]}{(discImage ? ".iso" : ".wav")}");
        if (path is not null) OutputPath = path;
    }

    private async Task ExportPngAsync()
    {
        if (_target is not { } target) return;
        string? path = _shell.Dialogs?.PickSave("PNG Pattern|*.png", ".png", $"calibration-{target.Id[..8]}.png");
        if (path is null) return;
        await RunAsync("Exporting pattern…", async token =>
        {
            BitmapSource image = await Task.Run(() => CalibrationPatternRenderer.RenderEncodingImage(target, token), token);
            CalibrationPatternRenderer.SavePng(image, path);
            CalibrationSessionJson.Save(Path.ChangeExtension(path, ".calibration.json"), Session());
            _shell.RegisterOutput(path);
            _statusMessage = "PNG and calibration record exported. Use the generate or streaming burn buttons on this page to maintain coordinate scale.";
        });
    }

    private async Task GenerateImageAsync()
    {
        if (_target is not { } target) return;
        if (string.IsNullOrWhiteSpace(OutputPath)) BrowseOutput();
        if (string.IsNullOrWhiteSpace(OutputPath)) return;
        string path;
        try
        {
            path = Path.GetFullPath(OutputPath.Trim());
            string extension = Path.GetExtension(path).ToLowerInvariant();
            bool discImage = target.Parameters.Kind is CalibrationDiscKind.Dvd or CalibrationDiscKind.BluRay;
            if (discImage ? extension != ".iso" : extension is not (".wav" or ".raw"))
                throw new ArgumentException("Please choose a .iso file for DVD/BD or .wav / .raw for CD.");
        }
        catch (Exception ex) { Report(ex); return; }
        var session = Session();
        await RunAsync("Generating calibration file…", async token =>
        {
            string directory = Path.GetDirectoryName(path)!;
            Directory.CreateDirectory(directory);
            string staging = Path.Combine(directory, $".calibration-{Guid.NewGuid():N}");
            Directory.CreateDirectory(staging);
            try
            {
                string stagedPath = Path.Combine(staging, Path.GetFileName(path));
                await Task.Run(() =>
                {
                    if (target.Parameters.Kind == CalibrationDiscKind.Cd)
                    {
                        CdTrackGenerator.GeneratePattern(target.Sample, stagedPath, CdParameters(target), session.CdInterleave,
                            cancellationToken: token);
                    }
                    else if (target.Parameters.Kind == CalibrationDiscKind.BluRay)
                    {
                        BluRayDiscParameters disc = BluRayParameters(target.Parameters);
                        using FileStream output = new(stagedPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                        BluRayStreamingGenerator.GenerateToStream(
                            output,
                            new BluRayStreamingOptions(disc, 0, disc.TotalSectors),
                            BluRayPatternMapping(target),
                            progress: null,
                            cancellationToken: token);
                    }
                    else
                    {
                        using FileStream output = new(stagedPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                        DvdStreamingGenerator.GeneratePattern(target.Sample, output, DvdOptions(target), cancellationToken: token);
                    }
                    token.ThrowIfCancellationRequested();
                    CalibrationSessionJson.Save(Path.ChangeExtension(stagedPath, ".calibration.json"), session);
                }, token);
                token.ThrowIfCancellationRequested();
                CalibrationOutputPublisher.Publish(staging, directory);
                _sessionPath = Path.ChangeExtension(path, ".calibration.json");
                _shell.RegisterOutput(path);
                _statusMessage = "Calibration files generated. After burning, open session and import photo to rectify and trace.";
            }
            finally { Directory.Delete(staging, recursive: true); }
        });
    }

    internal static BluRayDiscParameters BluRayParameters(CalibrationParameters parameters)
    {
        if (parameters.Kind != CalibrationDiscKind.BluRay)
        {
            throw new ArgumentException("Calibration parameters are not a Blu-ray target.", nameof(parameters));
        }

        BluRayDiscParameters disc = new(
            checked((uint)parameters.Sectors),
            1,
            parameters.InnerRadiusMm,
            parameters.OuterRadiusMm);
        disc.Validate();
        return disc;
    }

    /// <summary>Full-disc calibration payload: pattern inside the saved drawing range, zeros
    /// outside, so the burn stream still covers every address.</summary>
    internal static BluRayPatternRangeMapping BluRayPatternMapping(CalibrationTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);
        BluRayDiscParameters disc = BluRayParameters(target.Parameters);
        uint start = checked((uint)target.Parameters.DrawingStartSector);
        uint end = target.Parameters.DrawingEndSectorExclusive == 0
            ? disc.TotalSectors
            : checked((uint)target.Parameters.DrawingEndSectorExclusive);
        return new BluRayPatternRangeMapping(
            disc,
            target.Sample,
            start,
            end,
            target.Parameters.BluRayInvertMapping,
            // Saved targets and the fixed reference pixels are immutable.
            allowParallelSampling: true);
    }

    private void SaveSession()
    {
        if (_target is null) return;
        string? path = _shell.Dialogs?.PickSave("Calibration Session|*.calibration.json", ".calibration.json",
            _sessionPath is null ? $"calibration-{_target.Id[..8]}.calibration.json" : Path.GetFileName(_sessionPath));
        if (path is null) return;
        try { SaveSessionTo(path); _statusMessage = "Saved pattern, parameters, photo, boundaries, and curve points."; }
        catch (Exception ex) { Report(ex); }
    }

    internal void SaveSessionTo(string path)
    {
        CalibrationSessionJson.Save(path, Session());
        _sessionPath = path;
        _shell.RegisterOutput(path);
    }

    private async Task OpenSessionAsync()
    {
        string? path = _shell.Dialogs?.PickOpen(title: "Open Calibration Session", filter: "Calibration Session|*.calibration.json;*.json");
        if (path is null) return;
        await LoadSessionFromAsync(path);
    }

    private void PrepareStreamBurn()
    {
        if (_target is null) return;
        if (!EnsureSessionSavedForBurn()) return;
        // Burn family follows the material: selecting the calibration source is all the burn
        // workspace needs; navigation there refreshes the summary and device list itself.
        _shell.Burn.UseCalibrationPattern = true;
        _shell.NavigateTo(Workspace.Burn);
    }

    internal bool EnsureSessionSavedForBurn()
    {
        if (_target is null) return false;
        try
        {
            string? path = _sessionPath ?? _shell.Dialogs?.PickSave("Calibration Session|*.calibration.json", ".calibration.json",
                $"calibration-{_target.Id[..8]}.calibration.json");
            if (path is null) return false;
            SaveSessionTo(path);
            return true;
        }
        catch (Exception ex) { Report(ex); return false; }
    }

    internal static CdDiscParameters CdParameters(CalibrationTarget target) => new(target.Parameters.InnerRadiusMm,
        target.Parameters.OuterRadiusMm, target.Parameters.Sectors, target.Parameters.LinearDensity, 0,
        target.Parameters.OuterRadiusMm);
    internal static DvdStreamingOptions DvdOptions(CalibrationTarget target) => new(checked((uint)target.Parameters.Sectors),
        target.Parameters.InnerRadiusMm, target.Parameters.OuterRadiusMm, target.Parameters.LinearDensity, 0,
        PitchLinear: target.Parameters.PitchLinear, PitchQuadratic: target.Parameters.PitchQuadratic,
        PitchCubic: target.Parameters.PitchCubic);
    private static void ValidateOutputParameters(CalibrationParameters p)
    {
        p.Validate();
        if (p.Kind == CalibrationDiscKind.Dvd)
            new DvdStreamingOptions(checked((uint)p.Sectors), p.InnerRadiusMm, p.OuterRadiusMm, p.LinearDensity, 0,
                PitchLinear: p.PitchLinear, PitchQuadratic: p.PitchQuadratic, PitchCubic: p.PitchCubic).Validate();
        else new CdDiscParameters(p.InnerRadiusMm, p.OuterRadiusMm, p.Sectors, p.LinearDensity).Validate();
    }

    private async Task RunAsync(string message, Func<CancellationToken, Task> action)
    {
        if (IsBusy) return;
        _cancellation = new();
        _shell.SetBusy(true);
        _statusMessage = message;
        _shell.AppendLog($"[{DateTime.Now:HH:mm:ss}] Calibration: {message}\n");
        try { await action(_cancellation.Token); }
        catch (OperationCanceledException)
        {
            if (_isSolving) InvalidateFit();
            _statusMessage = "Operation cancelled.";
        }
        catch (Exception ex) { Report(ex); }
        finally
        {
            _isSolving = false;
            _cancellation.Dispose(); _cancellation = null;
            _shell.SetBusy(false);
            _shell.AppendLog($"[{DateTime.Now:HH:mm:ss}] Calibration: {_statusMessage}\n");
        }
    }

    private void Report(Exception ex)
    {
        _statusMessage = ex.Message;
        _shell.ShowToast("Calibration Incomplete", ex.Message, ToastKind.Error);
    }

    private void RefreshCommands()
    {
        CreateTargetCommand.NotifyCanExecuteChanged(); BrowseOutputCommand.NotifyCanExecuteChanged();
        ExportPngCommand.NotifyCanExecuteChanged(); GenerateImageCommand.NotifyCanExecuteChanged();
        StreamBurnCommand.NotifyCanExecuteChanged(); SaveSessionCommand.NotifyCanExecuteChanged();
        OpenSessionCommand.NotifyCanExecuteChanged(); SolveCommand.NotifyCanExecuteChanged();
        CancelCommand.NotifyCanExecuteChanged(); ApplyParametersCommand.NotifyCanExecuteChanged();
        RefreshPhotoCommands();
    }

    public sealed record CoarseCandidate(string Label, CalibrationParameters Parameters,
        double RotationRadians, double RmsErrorMm, double MaxErrorMm = 0, int OutlierCount = 0);
    public sealed record CoarseSearchRange(string Label, double RadiusRangeMm, bool Auto = false);
    private sealed record FitAttempt(CalibrationFitResult Result, double RangeMm, bool Expanded);
}
