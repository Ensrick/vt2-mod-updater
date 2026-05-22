using System.IO;
using System.IO.Compression;

namespace VT2ModUpdater.Services;

public static class Deployer
{
    public const string VersionSidecarFilename = "vt2updater_version.txt";

    /// <summary>
    /// VT2 mods load from any &lt;workshop&gt;/&lt;id&gt;/&lt;mod_name&gt;.mod regardless of Steam
    /// subscription state, so we deploy to a synthetic ID derived from the real Workshop
    /// ID. Steam doesn't manage synthetic folders (it has no record of them), so it can't
    /// revert or wipe our writes the way it does on real Workshop folders.
    ///
    /// Mapping: prefix "10" onto the real ID. 3712929235 (ct) -> 103712929235. Yields a 12+
    /// digit ID that's far outside the current real Workshop ID range (~3.7B) so it won't
    /// collide with any subscribed item.
    /// </summary>
    public static string SyntheticIdFor(string realWorkshopId) => "10" + realWorkshopId;

    public static string GetSyntheticFolder(string workshopContentRoot, string realWorkshopId)
        => Path.Combine(workshopContentRoot, SyntheticIdFor(realWorkshopId));

    public static string GetRealFolder(string workshopContentRoot, string realWorkshopId)
        => Path.Combine(workshopContentRoot, realWorkshopId);

    public static bool RealWorkshopFolderExists(string workshopContentRoot, string realWorkshopId)
        => Directory.Exists(GetRealFolder(workshopContentRoot, realWorkshopId));

    public static string? ReadInstalledVersion(string workshopContentRoot, string realWorkshopId)
    {
        var path = Path.Combine(GetSyntheticFolder(workshopContentRoot, realWorkshopId), VersionSidecarFilename);
        if (!File.Exists(path)) return null;
        var v = File.ReadAllText(path).Trim();
        return string.IsNullOrEmpty(v) ? null : v;
    }

    public static void DeployZipBytes(byte[] zipBytes, string workshopContentRoot, string realWorkshopId, string version)
    {
        var target = GetSyntheticFolder(workshopContentRoot, realWorkshopId);
        Directory.CreateDirectory(target);

        using var ms = new MemoryStream(zipBytes);
        using var archive = new ZipArchive(ms, ZipArchiveMode.Read);
        foreach (var entry in archive.Entries)
        {
            if (string.IsNullOrEmpty(entry.Name)) continue; // directory
            var safeName = Path.GetFileName(entry.FullName); // strip any path traversal
            if (string.IsNullOrEmpty(safeName)) continue;
            var outPath = Path.Combine(target, safeName);
            entry.ExtractToFile(outPath, overwrite: true);
        }

        File.WriteAllText(Path.Combine(target, VersionSidecarFilename), version);
    }
}
