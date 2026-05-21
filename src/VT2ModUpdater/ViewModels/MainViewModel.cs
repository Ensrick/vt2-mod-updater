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

    public MainViewModel()
    {
        RefreshCommand = new RelayCommand(async _ => await RefreshAsync());
        UpdateOneCommand = new RelayCommand(async p => { if (p is ModRow row) await UpdateOneAsync(row); });
        UpdateAllCommand = new RelayCommand(async _ => await UpdateAllAsync(), _ => Mods.Any(m => m.CanUpdate));
        OpenWorkshopFolderCommand = new RelayCommand(OpenWorkshopFolder, _ => !string.IsNullOrEmpty(_workshopContentRoot));

        _ = RefreshAsync();
    }

    private async Task RefreshAsync()
    {
        try
        {
            StatusMessage = "Locating Steam Workshop folder…";
            _workshopContentRoot = SteamPaths.FindWorkshopContentRoot();
            WorkshopPathDisplay = _workshopContentRoot is null
                ? "Workshop folder not found — subscribe to at least one VT2 Workshop item first"
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
                    row.WorkshopFolderExists = Deployer.ModFolderExists(_workshopContentRoot, entry.WorkshopId);
                    var installed = Deployer.ReadInstalledVersion(_workshopContentRoot, entry.WorkshopId);
                    if (!string.IsNullOrEmpty(installed)) row.InstalledVersion = installed;
                }
                Mods.Add(row);
            }

            var outOfDate = Mods.Count(m => m.CanUpdate);
            StatusMessage = outOfDate == 0
                ? $"All {Mods.Count} mods up to date"
                : $"{outOfDate} of {Mods.Count} mod(s) out of date";
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
            StatusMessage = $"Downloading {row.FriendlyName}…";
            var bytes = await _client.DownloadAssetAsync(_latestRelease, row.AssetFilename).ConfigureAwait(true);
            StatusMessage = $"Deploying {row.FriendlyName}…";
            Deployer.DeployZipBytes(bytes, _workshopContentRoot, row.WorkshopId, row.LatestVersion);
            row.InstalledVersion = row.LatestVersion;
            StatusMessage = $"Updated {row.FriendlyName} → {row.LatestVersion}";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Update failed for {row.FriendlyName}: {ex.Message}";
            MessageBox.Show(ex.Message, "Update failed", MessageBoxButton.OK, MessageBoxImage.Error);
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

    private void OpenWorkshopFolder(object? _)
    {
        if (string.IsNullOrEmpty(_workshopContentRoot) || !Directory.Exists(_workshopContentRoot)) return;
        Process.Start(new ProcessStartInfo { FileName = _workshopContentRoot, UseShellExecute = true });
    }
}
