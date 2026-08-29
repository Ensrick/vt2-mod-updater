using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Input;
using VT2ModUpdater.Models;
using VT2ModUpdater.Services;

namespace VT2ModUpdater.ViewModels;

public sealed class MainViewModel : ObservableObject, IDisposable
{
    private const int OperationIdle = 0;
    private const int OperationOrdinary = 1;
    private const int OperationSourceExactRecovery = 2;

    private readonly IReleaseClient _client;
    private readonly ISourceExactRecoveryRunner _sourceExactRecovery;
    private readonly Func<string?> _workshopPathResolver;
    private readonly object _sourceExactRecoveryGate = new();
    private GitHubRelease? _latestRelease;
    private CancellationTokenSource? _sourceExactRecoveryCancellation;
    private int _operationState;
    private long _manifestGeneration;
    private int _sourceExactRecoveryDisposed;
    private int _disposed;

    public ObservableCollection<ModRow> Mods { get; } = new();

    private string _statusMessage = "Loading…";
    public string StatusMessage { get => _statusMessage; set => Set(ref _statusMessage, value); }

    private string _releaseTagDisplay = "";
    public string ReleaseTagDisplay { get => _releaseTagDisplay; set => Set(ref _releaseTagDisplay, value); }

    private string _workshopPathDisplay = "";
    public string WorkshopPathDisplay { get => _workshopPathDisplay; set => Set(ref _workshopPathDisplay, value); }

    private string? _workshopContentRoot;

    private ModRow? _selectedMod;
    public ModRow? SelectedMod
    {
        get => _selectedMod;
        set
        {
            if (IsSourceExactOperationActive &&
                !ReferenceEquals(_selectedMod, value))
            {
                return;
            }
            if (!Set(ref _selectedMod, value)) return;
            if (!IsSourceExactOperationActive)
                SourceExactCommitInput = value?.LatestSourceCommit ?? "";
            OnPropertyChanged(nameof(SourceExactSelectionDisplay));
            CommandManager.InvalidateRequerySuggested();
        }
    }

    public string SourceExactSelectionDisplay => SelectedMod is null
        ? "No mod selected"
        : $"Selected: {SelectedMod.FriendlyName}";

    private string _sourceExactCommitInput = "";
    public string SourceExactCommitInput
    {
        get => _sourceExactCommitInput;
        set
        {
            if (Set(ref _sourceExactCommitInput, value))
                CommandManager.InvalidateRequerySuggested();
        }
    }

    private string _sourceExactRecoveryMessage =
        "Select a mod and enter an exact 40-character lowercase source commit.";
    public string SourceExactRecoveryMessage
    {
        get => _sourceExactRecoveryMessage;
        set => Set(ref _sourceExactRecoveryMessage, value);
    }

    private bool _isSourceExactRecoveryBusy;
    public bool IsSourceExactRecoveryBusy
    {
        get => _isSourceExactRecoveryBusy;
        private set
        {
            if (!Set(ref _isSourceExactRecoveryBusy, value)) return;
            OnPropertyChanged(nameof(CanEditSourceExactRecovery));
            CommandManager.InvalidateRequerySuggested();
        }
    }

    public bool CanEditSourceExactRecovery => !IsSourceExactRecoveryBusy;

    public RelayCommand RefreshCommand { get; }
    public RelayCommand UpdateOneCommand { get; }
    public RelayCommand UpdateAllCommand { get; }
    public RelayCommand OpenWorkshopFolderCommand { get; }
    public RelayCommand VerifyInstalledCommand { get; }
    public RelayCommand RecoverExactSourceCommand { get; }
    public RelayCommand CancelExactSourceRecoveryCommand { get; }

    public MainViewModel()
        : this(new SourceExactRecoveryRunner(), null, startRefresh: true) { }

    internal MainViewModel(
        ISourceExactRecoveryRunner sourceExactRecovery,
        string? workshopContentRoot = null,
        bool startRefresh = false,
        IReleaseClient? releaseClient = null,
        Func<string?>? workshopPathResolver = null)
    {
        _sourceExactRecovery = sourceExactRecovery ??
            throw new ArgumentNullException(nameof(sourceExactRecovery));
        _client = releaseClient ?? new GitHubReleaseClient();
        _workshopPathResolver = workshopPathResolver ??
            SteamPaths.FindWorkshopContentRoot;
        _workshopContentRoot = workshopContentRoot;
        if (!string.IsNullOrWhiteSpace(workshopContentRoot))
            WorkshopPathDisplay = workshopContentRoot;

        RefreshCommand = new RelayCommand(
            async _ => await RefreshWithAdmissionAsync(),
            _ => CanStartExclusiveOperation());
        UpdateOneCommand = new RelayCommand(
            async p =>
            {
                if (p is ModRow row)
                    await UpdateOneWithAdmissionAsync(row);
            },
            p => CanStartExclusiveOperation() &&
                p is ModRow row && row.CanUpdate);
        UpdateAllCommand = new RelayCommand(
            async _ => await UpdateAllWithAdmissionAsync(),
            _ => CanStartExclusiveOperation() && Mods.Any(m => m.CanUpdate));
        OpenWorkshopFolderCommand = new RelayCommand(OpenWorkshopFolder, _ => !string.IsNullOrEmpty(_workshopContentRoot));
        VerifyInstalledCommand = new RelayCommand(
            _ => VerifyInstalledBundlesWithAdmission(),
            _ => CanStartExclusiveOperation() &&
                !string.IsNullOrEmpty(_workshopContentRoot) && Mods.Count > 0);
        RecoverExactSourceCommand = new RelayCommand(
            async _ => await RecoverExactSourceAsync(),
            _ => CanRecoverExactSource());
        CancelExactSourceRecoveryCommand = new RelayCommand(
            _ => CancelExactSourceRecovery(),
            _ => IsSourceExactRecoveryBusy);

        if (startRefresh)
            _ = RefreshWithAdmissionAsync();
    }

    private bool IsSourceExactOperationActive =>
        Volatile.Read(ref _operationState) == OperationSourceExactRecovery;

    private bool CanStartExclusiveOperation() =>
        Volatile.Read(ref _disposed) == 0 &&
        Volatile.Read(ref _operationState) == OperationIdle;

    private bool TryBeginOperation(int operation) =>
        Volatile.Read(ref _disposed) == 0 &&
        Interlocked.CompareExchange(
            ref _operationState,
            operation,
            OperationIdle) == OperationIdle;

    private void EndOperation(int operation)
    {
        var previous = Interlocked.CompareExchange(
            ref _operationState,
            OperationIdle,
            operation);
        if (previous != operation)
        {
            throw new InvalidOperationException(
                "updater operation admission state was released by the wrong owner");
        }
        CommandManager.InvalidateRequerySuggested();
    }

    internal async Task RefreshWithAdmissionAsync()
    {
        if (!TryBeginOperation(OperationOrdinary))
        {
            StatusMessage = "Another updater operation is already running.";
            return;
        }
        CommandManager.InvalidateRequerySuggested();
        try
        {
            await RefreshAsync();
        }
        finally
        {
            EndOperation(OperationOrdinary);
        }
    }

    private async Task RefreshAsync()
    {
        try
        {
            StatusMessage = "Locating Steam Workshop folder…";
            _workshopContentRoot = _workshopPathResolver();
            WorkshopPathDisplay = _workshopContentRoot is null
                ? "Workshop folder not found — install/run VT2 at least once so Steam creates 552500"
                : _workshopContentRoot;

            StatusMessage = "Fetching latest release from GitHub…";
            _latestRelease = await _client.GetLatestReleaseAsync().ConfigureAwait(true);
            var manifest = await _client.DownloadManifestAsync(_latestRelease).ConfigureAwait(true);
            ReleaseTagDisplay = $"release: {manifest.ReleaseTag}";

            var refreshedRows = new List<ModRow>(manifest.Mods.Count);
            foreach (var entry in manifest.Mods)
            {
                var row = new ModRow(entry);
                if (_workshopContentRoot is not null)
                {
                    row.RealWorkshopSubscribed = Deployer.RealWorkshopFolderExists(_workshopContentRoot, entry.WorkshopId);
                    var installed = Deployer.ReadInstalledVersion(_workshopContentRoot, entry.WorkshopId);
                    if (!string.IsNullOrEmpty(installed)) row.InstalledVersion = installed;
                }
                refreshedRows.Add(row);
            }

            SelectedMod = null;
            Mods.Clear();
            foreach (var row in refreshedRows)
                Mods.Add(row);
            Interlocked.Increment(ref _manifestGeneration);
            SelectedMod = Mods.FirstOrDefault();

            var outOfDate = Mods.Count(m => m.CanUpdate);
            var alsoSubscribed = Mods.Count(m => m.RealWorkshopSubscribed);
            StatusMessage = (outOfDate == 0 ? $"All {Mods.Count} mods up to date" : $"{outOfDate} of {Mods.Count} mod(s) out of date")
                            + (alsoSubscribed > 0 ? $"  ·  {alsoSubscribed} also subscribed on Workshop (unsubscribe to avoid double-load)" : "");
        }
        catch (Exception ex)
        {
            StatusMessage = $"Error: {ex.Message}";
        }
    }

    internal async Task UpdateOneWithAdmissionAsync(ModRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        if (!TryBeginOperation(OperationOrdinary))
        {
            StatusMessage = "Another updater operation is already running.";
            return;
        }
        CommandManager.InvalidateRequerySuggested();
        try
        {
            await UpdateOneAsync(row);
        }
        finally
        {
            EndOperation(OperationOrdinary);
        }
    }

    private async Task UpdateOneAsync(ModRow row)
    {
        if (_workshopContentRoot is null || _latestRelease is null) return;
        try
        {
            // Download + integrity verify. On mismatch we retry once before giving up —
            // GitHub's CDN occasionally serves a partial body and a clean re-fetch usually
            // resolves it. After two mismatches we surface a user-visible warning and skip
            // (the install stays on the previous version; user can re-run the updater).
            var bytes = await DownloadAndVerifyAsync(row).ConfigureAwait(true);
            if (bytes is null) return; // status + warning already surfaced

            StatusMessage = $"Deploying {row.FriendlyName}…";
            // Pass ExpectedSha256 so the deployer can stash it in the integrity sidecar
            // (Issue #32). After a successful deploy this row's verify state is implicitly
            // OK, so clear any prior verify badge — the user will hit "Verify installed
            // bundles" again if they want a fresh pass.
            Deployer.DeployZipBytes(bytes, _workshopContentRoot, row.WorkshopId, row.LatestVersion, row.ExpectedSha256);
            row.InstalledVersion = row.LatestVersion;
            row.VerifyState = null;
            StatusMessage = $"Updated {row.FriendlyName} → {row.LatestVersion}";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Update failed for {row.FriendlyName}: {ex.Message}";
            MessageBox.Show(ex.Message, "Update failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    /// <summary>
    /// Downloads <paramref name="row"/>'s zip asset and verifies it against the
    /// manifest's <c>sha256</c>. Retries once on mismatch. Returns the verified bytes,
    /// or null when both attempts mismatch (in which case the status bar + a MessageBox
    /// already explain to the user). Throws on transport errors — caller's try/catch
    /// handles those.
    /// </summary>
    private async Task<byte[]?> DownloadAndVerifyAsync(ModRow row)
    {
        if (_latestRelease is null) return null;

        for (var attempt = 1; attempt <= 2; attempt++)
        {
            StatusMessage = attempt == 1
                ? $"Downloading {row.FriendlyName}…"
                : $"Re-downloading {row.FriendlyName} (integrity mismatch)…";

            var bytes = await _client.DownloadAssetAsync(_latestRelease, row.AssetFilename).ConfigureAwait(true);
            var check = Deployer.VerifyBundleIntegrity(bytes, row.ExpectedSha256);

            switch (check.Result)
            {
                case IntegrityResult.Matched:
                    Debug.WriteLine($"[integrity] {row.AssetFilename} matched ({check.ComputedSha256})");
                    return bytes;

                case IntegrityResult.SkippedNoExpectedHash:
                    // Older manifest predates the sha256 field — proceed without verification.
                    Debug.WriteLine($"[integrity] manifest entry for {row.AssetFilename} missing sha256 — skipping integrity check");
                    return bytes;

                case IntegrityResult.MalformedExpected:
                    // A malformed authority value cannot safely authorize bytes. A
                    // re-download cannot repair the manifest, so fail immediately.
                    var malformed = $"The release manifest contains an invalid SHA-256 for {row.AssetFilename}. "
                                  + "The bundle was not installed. Please report the release-manifest error.";
                    Debug.WriteLine($"[integrity] {malformed} Value='{row.ExpectedSha256}'");
                    StatusMessage = $"{row.FriendlyName}: invalid manifest SHA-256 — skipped";
                    MessageBox.Show(malformed, "Invalid release manifest", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return null;

                case IntegrityResult.Mismatch:
                    Debug.WriteLine($"[integrity] {row.AssetFilename} MISMATCH attempt {attempt}/2 — expected {check.ExpectedSha256}, got {check.ComputedSha256}");
                    if (attempt == 1)
                    {
                        StatusMessage = $"Bundle integrity mismatch for {row.FriendlyName} — re-downloading…";
                        continue;
                    }
                    var msg = $"Bundle {row.AssetFilename} failed integrity check after 2 attempts. Skipping. Please re-run the updater later.\n\n"
                              + $"Expected: {check.ExpectedSha256}\nGot:      {check.ComputedSha256}";
                    StatusMessage = $"{row.FriendlyName}: bundle integrity mismatch after 2 attempts — skipped";
                    MessageBox.Show(msg, "Bundle integrity check failed", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return null;
            }
        }
        return null;
    }

    private async Task UpdateAllWithAdmissionAsync()
    {
        if (!TryBeginOperation(OperationOrdinary))
        {
            StatusMessage = "Another updater operation is already running.";
            return;
        }
        CommandManager.InvalidateRequerySuggested();
        try
        {
            await UpdateAllAsync();
        }
        finally
        {
            EndOperation(OperationOrdinary);
        }
    }

    private async Task UpdateAllAsync()
    {
        var targets = Mods.Where(m => m.CanUpdate).ToList();
        for (var i = 0; i < targets.Count; i++)
        {
            StatusMessage = $"[{i + 1}/{targets.Count}] {targets[i].FriendlyName}";
            await UpdateOneAsync(targets[i]);
        }
        var failed = Mods.Count(m => m.CanUpdate);
        StatusMessage = failed == 0
            ? $"Updated {targets.Count} mod(s) — all current"
            : $"Updated {targets.Count - failed}/{targets.Count} — {failed} still out of date";
    }

    internal async Task RecoverExactSourceAsync()
    {
        if (!HasValidRecoveryInput())
        {
            SourceExactRecoveryMessage =
                "Select a mod, locate the Workshop folder, and enter an exact " +
                "40-character lowercase source commit.";
            return;
        }
        if (!TryBeginOperation(OperationSourceExactRecovery))
        {
            var activeOperation = Volatile.Read(ref _operationState);
            SourceExactRecoveryMessage =
                activeOperation == OperationSourceExactRecovery
                    ? "A source-exact recovery is already running."
                    : "Another updater operation is already running.";
            return;
        }
        CommandManager.InvalidateRequerySuggested();

        var row = SelectedMod!;
        var commit = SourceExactCommitInput;
        var manifestGeneration = Volatile.Read(ref _manifestGeneration);
        if (!Mods.Contains(row) ||
            !SourceExactRecoveryRequestContract.IsCanonicalSourceCommit(commit))
        {
            SourceExactRecoveryMessage =
                "The recovery selection or exact commit changed before admission; refresh and select it again.";
            EndOperation(OperationSourceExactRecovery);
            return;
        }
        var request = new SourceExactRecoveryRequest(
            RecoveryRecordContract.Repository,
            row.Entry.ModId,
            row.WorkshopId,
            commit,
            _workshopContentRoot!);
        var cancellation = new CancellationTokenSource();
        lock (_sourceExactRecoveryGate)
        {
            if (Volatile.Read(ref _disposed) != 0)
            {
                cancellation.Dispose();
                EndOperation(OperationSourceExactRecovery);
                return;
            }
            _sourceExactRecoveryCancellation = cancellation;
        }

        IsSourceExactRecoveryBusy = true;
        SourceExactRecoveryMessage =
            $"Recovering {row.FriendlyName} at exact source {commit}…";
        StatusMessage = SourceExactRecoveryMessage;
        try
        {
            var result = await _sourceExactRecovery.RecoverAndVerifyAsync(
                request,
                cancellation.Token).ConfigureAwait(true);
            SourceExactRecoveryMessage = result.Message;
            StatusMessage = $"{row.FriendlyName}: {result.Message}";

            if (result.Status == SourceExactRecoveryRunStatus.Succeeded &&
                result.ReadBack is not null)
            {
                if (manifestGeneration == Volatile.Read(ref _manifestGeneration) &&
                    Mods.Contains(row) &&
                    ReferenceEquals(SelectedMod, row))
                {
                    row.InstalledVersion = result.ReadBack.InstalledVersion;
                    row.InstalledSourceCommit = result.ReadBack.State.SourceCommit;
                    row.VerifyState = null;
                }
                else
                {
                    SourceExactRecoveryMessage =
                        "Exact source was installed and read back, but the manifest selection changed; refresh before relying on the displayed row state.";
                    StatusMessage = SourceExactRecoveryMessage;
                }
            }
        }
        catch (Exception ex)
        {
            SourceExactRecoveryMessage =
                $"Source-exact recovery failed before a terminal result: {ex.Message}";
            StatusMessage = $"{row.FriendlyName}: {SourceExactRecoveryMessage}";
        }
        finally
        {
            lock (_sourceExactRecoveryGate)
            {
                if (ReferenceEquals(
                        _sourceExactRecoveryCancellation,
                        cancellation))
                {
                    _sourceExactRecoveryCancellation = null;
                }
            }
            cancellation.Dispose();
            IsSourceExactRecoveryBusy = false;
            EndOperation(OperationSourceExactRecovery);
            DisposeSourceExactRecoveryAfterCompletionIfRequested();
        }
    }

    internal void CancelExactSourceRecovery()
    {
        lock (_sourceExactRecoveryGate)
        {
            if (_sourceExactRecoveryCancellation is null) return;
            SourceExactRecoveryMessage = "Cancelling source-exact recovery…";
            _sourceExactRecoveryCancellation.Cancel();
        }
    }

    private bool CanRecoverExactSource() =>
        Volatile.Read(ref _operationState) == OperationIdle &&
        HasValidRecoveryInput();

    private bool HasValidRecoveryInput() =>
        Volatile.Read(ref _disposed) == 0 &&
        SelectedMod is not null &&
        Mods.Contains(SelectedMod) &&
        !string.IsNullOrWhiteSpace(_workshopContentRoot) &&
        SourceExactRecoveryRequestContract.IsCanonicalSourceCommit(
            SourceExactCommitInput);

    /// <summary>
    /// Issue #32: post-install verification. For each row, classify the installed bundle
    /// as OK / OUT_OF_DATE / TAMPERED / NO_SIDECAR / NOT_INSTALLED by comparing:
    ///   (a) the deploy-time hash stashed in <c>.vt2updater_sha256.txt</c>, against
    ///   (b) the current Merkle-style hash of the installed files, and
    ///   (c) the latest manifest's <c>sha256</c>.
    /// Surfaces per-row results via <c>ModRow.VerifyState</c> and writes per-category
    /// counts into the status bar. Never auto-triggers a re-download — TAMPERED is
    /// surfaced and the user decides whether to click Update (the user may have
    /// intentionally modified the bundle).
    /// </summary>
    private void VerifyInstalledBundlesWithAdmission()
    {
        if (!TryBeginOperation(OperationOrdinary))
        {
            StatusMessage = "Another updater operation is already running.";
            return;
        }
        CommandManager.InvalidateRequerySuggested();
        try
        {
            VerifyInstalledBundles();
        }
        finally
        {
            EndOperation(OperationOrdinary);
        }
    }

    private void VerifyInstalledBundles()
    {
        if (_workshopContentRoot is null)
        {
            StatusMessage = "Cannot verify — workshop folder not found.";
            return;
        }

        int ok = 0, outOfDate = 0, tampered = 0, noSidecar = 0, notInstalled = 0;
        foreach (var row in Mods)
        {
            try
            {
                var result = Deployer.VerifyInstalled(_workshopContentRoot, row.WorkshopId, row.ExpectedSha256);
                row.VerifyState = result.State;
                switch (result.State)
                {
                    case Services.VerifyState.Ok: ok++; break;
                    case Services.VerifyState.OutOfDate: outOfDate++; break;
                    case Services.VerifyState.Tampered:
                        tampered++;
                        Debug.WriteLine($"[verify] {row.FriendlyName} TAMPERED — stashed installed_files={result.StashedInstalledFilesSha256}, current={result.ComputedInstalledFilesSha256}");
                        break;
                    case Services.VerifyState.NoSidecar: noSidecar++; break;
                    case Services.VerifyState.NotInstalled: notInstalled++; break;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[verify] {row.FriendlyName} threw: {ex}");
                row.VerifyState = Services.VerifyState.NoSidecar; // best-effort: treat unreadable as no record
                noSidecar++;
            }
        }

        var parts = new List<string>();
        if (ok > 0) parts.Add($"{ok} OK");
        if (outOfDate > 0) parts.Add($"{outOfDate} OUT_OF_DATE");
        if (tampered > 0) parts.Add($"{tampered} TAMPERED");
        if (noSidecar > 0) parts.Add($"{noSidecar} NO_SIDECAR");
        if (notInstalled > 0) parts.Add($"{notInstalled} NOT_INSTALLED");
        StatusMessage = "Verification: " + (parts.Count > 0 ? string.Join("  ·  ", parts) : "nothing to check");

        if (tampered > 0)
        {
            MessageBox.Show(
                $"{tampered} mod(s) have been modified since install.\n\nThis is not necessarily malicious — you may have edited files intentionally. If unintended, click Update to re-install from GitHub.",
                "Tampered bundles detected",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }

    private void OpenWorkshopFolder(object? _)
    {
        if (string.IsNullOrEmpty(_workshopContentRoot) || !Directory.Exists(_workshopContentRoot)) return;
        Process.Start(new ProcessStartInfo { FileName = _workshopContentRoot, UseShellExecute = true });
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

        lock (_sourceExactRecoveryGate)
            _sourceExactRecoveryCancellation?.Cancel();

        if (Volatile.Read(ref _operationState) != OperationSourceExactRecovery)
            DisposeSourceExactRecovery();
        _client.Dispose();
    }

    private void DisposeSourceExactRecoveryAfterCompletionIfRequested()
    {
        if (Volatile.Read(ref _disposed) != 0)
            DisposeSourceExactRecovery();
    }

    private void DisposeSourceExactRecovery()
    {
        if (Interlocked.Exchange(ref _sourceExactRecoveryDisposed, 1) == 0)
            _sourceExactRecovery.Dispose();
    }
}
