using VT2ModUpdater.ViewModels;

namespace VT2ModUpdater.Models;

public sealed class ModRow : ObservableObject
{
    public ManifestEntry Entry { get; }
    public string FriendlyName => Entry.FriendlyName;
    public string LatestVersion => Entry.Version;
    public string WorkshopId => Entry.WorkshopId;
    public string AssetFilename => Entry.AssetFilename;

    private string _installedVersion = "—";
    public string InstalledVersion
    {
        get => _installedVersion;
        set { _installedVersion = value; OnPropertyChanged(); OnPropertyChanged(nameof(StateLabel)); OnPropertyChanged(nameof(CanUpdate)); }
    }

    private bool _workshopFolderExists;
    public bool WorkshopFolderExists
    {
        get => _workshopFolderExists;
        set { _workshopFolderExists = value; OnPropertyChanged(); OnPropertyChanged(nameof(StateLabel)); OnPropertyChanged(nameof(CanUpdate)); }
    }

    public string StateLabel
    {
        get
        {
            if (!WorkshopFolderExists) return "Not subscribed — open Workshop and subscribe first";
            if (InstalledVersion == "—") return "Installed (version unknown — needs first update)";
            if (string.Equals(InstalledVersion, LatestVersion, StringComparison.OrdinalIgnoreCase)) return "Up to date";
            return "Out of date";
        }
    }

    public bool CanUpdate =>
        WorkshopFolderExists &&
        !string.Equals(InstalledVersion, LatestVersion, StringComparison.OrdinalIgnoreCase);

    public ModRow(ManifestEntry entry) { Entry = entry; }
}
