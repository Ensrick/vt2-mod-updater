using System.IO;
using System.IO.Compression;

namespace VT2ModUpdater.Services;

public static class Deployer
{
    public const string VersionSidecarFilename = "vt2updater_version.txt";

    public static string GetModFolder(string workshopContentRoot, string workshopId)
        => Path.Combine(workshopContentRoot, workshopId);

    public static bool ModFolderExists(string workshopContentRoot, string workshopId)
        => Directory.Exists(GetModFolder(workshopContentRoot, workshopId));

    public static string? ReadInstalledVersion(string workshopContentRoot, string workshopId)
    {
        var path = Path.Combine(GetModFolder(workshopContentRoot, workshopId), VersionSidecarFilename);
        if (!File.Exists(path)) return null;
        var v = File.ReadAllText(path).Trim();
        return string.IsNullOrEmpty(v) ? null : v;
    }

    public static void DeployZipBytes(byte[] zipBytes, string workshopContentRoot, string workshopId, string version)
    {
        var target = GetModFolder(workshopContentRoot, workshopId);
        if (!Directory.Exists(target))
            throw new DirectoryNotFoundException(
                $"Workshop folder for mod {workshopId} does not exist. Subscribe to the mod on Steam first so Steam creates the folder.");

        using var ms = new MemoryStream(zipBytes);
        using var archive = new ZipArchive(ms, ZipArchiveMode.Read);
        foreach (var entry in archive.Entries)
        {
            if (string.IsNullOrEmpty(entry.Name)) continue; // directory
            var outPath = Path.Combine(target, entry.FullName.Replace('/', Path.DirectorySeparatorChar));
            var outDir = Path.GetDirectoryName(outPath);
            if (!string.IsNullOrEmpty(outDir)) Directory.CreateDirectory(outDir);
            entry.ExtractToFile(outPath, overwrite: true);
        }

        File.WriteAllText(Path.Combine(target, VersionSidecarFilename), version);
    }
}
