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

    private bool _realWorkshopSubscribed;
    public bool RealWorkshopSubscribed
    {
        get => _realWorkshopSubscribed;
        set { _realWorkshopSubscribed = value; OnPropertyChanged(); OnPropertyChanged(nameof(StateLabel)); }
    }

    public string StateLabel
    {
        get
        {
            string baseState;
            if (InstalledVersion == "—") baseState = "Not installed";
            else if (string.Equals(InstalledVersion, LatestVersion, StringComparison.OrdinalIgnoreCase)) baseState = "Up to date";
            else baseState = "Out of date";

            return RealWorkshopSubscribed
                ? baseState + " — also subscribed on Workshop (unsubscribe to avoid double-load)"
                : baseState;
        }
    }

    public bool CanUpdate => !string.Equals(InstalledVersion, LatestVersion, StringComparison.OrdinalIgnoreCase);

    public ModRow(ManifestEntry entry) { Entry = entry; }
}
