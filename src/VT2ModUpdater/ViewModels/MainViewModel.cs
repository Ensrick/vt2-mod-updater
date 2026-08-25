using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using VT2ModUpdater.Models;
using VT2ModUpdater.Services;

namespace VT2ModUpdater.ViewModels;

public sealed class MainViewModel : ObservableObject
{
    private readonly GitHubReleaseClient _client = new();
    private GitHubRelease? _latestRelease;

    public ObservableCollection<ModRow> Mods { get; } = new();

    private string _statusMessage = "Loading…";
    public string StatusMessage { get => _statusMessage; set => Set(ref _statusMessage, value); }

    private string _releaseTagDisplay = "";
    public string ReleaseTagDisplay { get => _releaseTagDisplay; set => Set(ref _releaseTagDisplay, value); }

    private string _workshopPathDisplay = "";
    public string WorkshopPathDisplay { get => _workshopPathDisplay; set => Set(ref _workshopPathDisplay, value); }

    private string? _workshopContentRoot;

    public RelayCommand RefreshCommand { get; }
    public RelayCommand UpdateOneCommand { get; }
    public RelayCommand UpdateAllCommand { get; }
    public RelayCommand OpenWorkshopFolderCommand { get; }
    public RelayCommand VerifyInstalledCommand { get; }

    public MainViewModel()
    {
        RefreshCommand = new RelayCommand(async _ => await RefreshAsync());
        UpdateOneCommand = new RelayCommand(async p => { if (p is ModRow row) await UpdateOneAsync(row); });
        UpdateAllCommand = new RelayCommand(async _ => await UpdateAllAsync(), _ => Mods.Any(m => m.CanUpdate));
        OpenWorkshopFolderCommand = new RelayCommand(OpenWorkshopFolder, _ => !string.IsNullOrEmpty(_workshopContentRoot));
        VerifyInstalledCommand = new RelayCommand(_ => VerifyInstalledBundles(), _ => !string.IsNullOrEmpty(_workshopContentRoot) && Mods.Count > 0);

        _ = RefreshAsync();
    }

    private async Task RefreshAsync()
    {
        try
        {
            StatusMessage = "Locating Steam Workshop folder…";
            _workshopContentRoot = SteamPaths.FindWorkshopContentRoot();
            WorkshopPathDisplay = _workshopContentRoot is null
                ? "Workshop folder not found — install/run VT2 at least once so Steam creates 552500"
                : _workshopContentRoot;

            StatusMessage = "Fetching latest release from GitHub…";
            _latestRelease = await _client.GetLatestReleaseAsync().ConfigureAwait(true);
            var manifest = await _client.DownloadManifestAsync(_latestRelease).ConfigureAwait(true);
            ReleaseTagDisplay = $"release: {manifest.ReleaseTag}";

            Mods.Clear();
            foreach (var entry in manifest.Mods)
            {
                var row = new ModRow(entry);
                if (_workshopContentRoot is not null)
                {
                    row.RealWorkshopSubscribed = Deployer.RealWorkshopFolderExists(_workshopContentRoot, entry.WorkshopId);
                    var installed = Deployer.ReadInstalledVersion(_workshopContentRoot, entry.WorkshopId);
                    if (!string.IsNullOrEmpty(installed)) row.InstalledVersion = installed;
                }
                Mods.Add(row);
            }

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
}
