using VT2ModUpdater.Services;
using VT2ModUpdater.ViewModels;

namespace VT2ModUpdater.Models;

public sealed class ModRow : ObservableObject
{
    public ManifestEntry Entry { get; }
    public string FriendlyName => Entry.FriendlyName;
    public string LatestVersion => Entry.Version;
    public string WorkshopId => Entry.WorkshopId;
    public string AssetFilename => Entry.AssetFilename;
    /// <summary>Manifest's lowercase-hex SHA-256, or null on older releases.</summary>
    public string? ExpectedSha256 => Entry.Sha256;

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

    /// <summary>
    /// Result of the most recent "Verify installed bundles" pass. Null until the user
    /// clicks the button. Drives both <see cref="StateLabel"/> and <see cref="StatusColor"/>
    /// when populated.
    /// </summary>
    private VerifyState? _verifyState;
    public VerifyState? VerifyState
    {
        get => _verifyState;
        set
        {
            _verifyState = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(StateLabel));
            OnPropertyChanged(nameof(StatusColor));
            OnPropertyChanged(nameof(VerifyStateLabel));
            OnPropertyChanged(nameof(CanUpdate));
        }
    }

    public string StateLabel
    {
        get
        {
            string baseState;
            if (_verifyState is { } vs)
            {
                baseState = vs switch
                {
                    Services.VerifyState.Ok => "OK — verified",
                    Services.VerifyState.OutOfDate => "OUT_OF_DATE — newer bundle available",
                    Services.VerifyState.Tampered => "TAMPERED — installed bundle modified after install",
                    Services.VerifyState.NoSidecar => "NO_SIDECAR — installed without integrity record",
                    Services.VerifyState.NotInstalled => "NOT_INSTALLED",
                    _ => "",
                };
            }
            else if (InstalledVersion == "—") baseState = "Not installed";
            else if (string.Equals(InstalledVersion, LatestVersion, StringComparison.OrdinalIgnoreCase)) baseState = "Up to date";
            else baseState = "Out of date";

            return RealWorkshopSubscribed
                ? baseState + " — also subscribed on Workshop (unsubscribe to avoid double-load)"
                : baseState;
        }
    }

    /// <summary>
    /// Short label for the verify result (used in tighter UI surfaces). Returns empty
    /// string before the first verify pass.
    /// </summary>
    public string VerifyStateLabel => _verifyState switch
    {
        Services.VerifyState.Ok => "OK",
        Services.VerifyState.OutOfDate => "OUT_OF_DATE",
        Services.VerifyState.Tampered => "TAMPERED",
        Services.VerifyState.NoSidecar => "NO_SIDECAR",
        Services.VerifyState.NotInstalled => "NOT_INSTALLED",
        _ => "",
    };

    /// <summary>
    /// Foreground color hex for the verify-state pill. Green / yellow / red / gray map
    /// to the five categories; empty string before the first verify pass so the binding
    /// falls back to the default.
    /// </summary>
    public string StatusColor => _verifyState switch
    {
        Services.VerifyState.Ok => "#4CAF50",          // green
        Services.VerifyState.OutOfDate => "#FFB74D",   // amber
        Services.VerifyState.Tampered => "#E53935",    // red
        Services.VerifyState.NoSidecar => "#90A4AE",   // blue-gray
        Services.VerifyState.NotInstalled => "#777",   // muted
        _ => "#EEE",                                   // default fg
    };

    // A verification failure must make the row repairable even when the version
    // sidecar still equals LatestVersion. Otherwise the UI tells the user to click
    // Update while leaving that button disabled for tampered, stale-hash, and legacy
    // no-sidecar installs.
    public bool CanUpdate => _verifyState is Services.VerifyState.OutOfDate
        or Services.VerifyState.Tampered
        or Services.VerifyState.NoSidecar
        or Services.VerifyState.NotInstalled
        || !string.Equals(InstalledVersion, LatestVersion, StringComparison.OrdinalIgnoreCase);

    public ModRow(ManifestEntry entry) { Entry = entry; }
}
