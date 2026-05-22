using System.IO;
using System.IO.Compression;
using System.Text.RegularExpressions;

namespace VT2ModUpdater.Services;

public static class Deployer
{
    public const string VersionSidecarFilename = "vt2updater_version.txt";
    public const string SyntheticIdPrefix = "10";

    /// <summary>50 MB sanity cap on any single mod zip. Real bundles are well under 5 MB.</summary>
    public const long MaxZipBytes = 50L * 1024 * 1024;

    /// <summary>200 MB cap on total uncompressed size — protects against zip bombs.</summary>
    public const long MaxUncompressedBytes = 200L * 1024 * 1024;

    /// <summary>500 entries per zip — bundle has &lt;10 in practice.</summary>
    public const int MaxEntryCount = 500;

    private static readonly Regex WorkshopIdPattern = new("^[0-9]+$", RegexOptions.Compiled);

    /// <summary>
    /// VT2 mods load from any &lt;workshop&gt;/&lt;id&gt;/&lt;mod_name&gt;.mod regardless of Steam
    /// subscription state, so we deploy to a synthetic ID derived from the real Workshop
    /// ID. Steam doesn't manage synthetic folders (no Workshop publish record) so it can't
    /// revert or wipe writes the way it does on real Workshop folders.
    /// Mapping: prefix "10" onto the real ID. 3712929235 (ct) -&gt; 103712929235.
    /// </summary>
    public static string SyntheticIdFor(string realWorkshopId)
    {
        ValidateRealWorkshopId(realWorkshopId);
        return SyntheticIdPrefix + realWorkshopId;
    }

    public static string GetSyntheticFolder(string workshopContentRoot, string realWorkshopId)
        => Path.Combine(workshopContentRoot, SyntheticIdFor(realWorkshopId));

    public static string GetRealFolder(string workshopContentRoot, string realWorkshopId)
    {
        ValidateRealWorkshopId(realWorkshopId);
        return Path.Combine(workshopContentRoot, realWorkshopId);
    }

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
        if (zipBytes is null || zipBytes.Length == 0)
            throw new DeployException("Empty zip payload");
        if (zipBytes.Length > MaxZipBytes)
            throw new DeployException($"Zip is {zipBytes.Length:N0} bytes — over the {MaxZipBytes:N0} byte sanity cap. Refusing to extract.");
        if (string.IsNullOrWhiteSpace(workshopContentRoot) || !Directory.Exists(workshopContentRoot))
            throw new DeployException($"Workshop content root does not exist: '{workshopContentRoot}'");
        if (string.IsNullOrWhiteSpace(version))
            throw new DeployException("Version string is required");

        var target = GetSyntheticFolder(workshopContentRoot, realWorkshopId);
        AssertTargetIsSynthetic(target, workshopContentRoot, realWorkshopId);

        using var ms = new MemoryStream(zipBytes);
        using var archive = new ZipArchive(ms, ZipArchiveMode.Read);
        ValidateZipEntries(archive);

        Directory.CreateDirectory(target);
        foreach (var entry in archive.Entries)
        {
            if (string.IsNullOrEmpty(entry.Name)) continue;
            var safeName = Path.GetFileName(entry.FullName);
            if (string.IsNullOrEmpty(safeName)) continue;
            var outPath = Path.Combine(target, safeName);
            entry.ExtractToFile(outPath, overwrite: true);
        }

        File.WriteAllText(Path.Combine(target, VersionSidecarFilename), version);
    }

    /// <summary>
    /// Belt-and-suspenders check: confirms the resolved target path is the synthetic
    /// folder, not the real one. If anyone ever refactors SyntheticIdFor and a bug lets
    /// the real ID through, this fires before we touch the filesystem. v0.1.0 regression
    /// guard.
    /// </summary>
    internal static void AssertTargetIsSynthetic(string target, string workshopContentRoot, string realWorkshopId)
    {
        ValidateRealWorkshopId(realWorkshopId);
        var expected = Path.Combine(workshopContentRoot, SyntheticIdPrefix + realWorkshopId);
        if (!string.Equals(Path.GetFullPath(target), Path.GetFullPath(expected), StringComparison.OrdinalIgnoreCase))
            throw new DeployException($"Refusing to deploy: target '{target}' is not the synthetic folder '{expected}'");
        var leafName = Path.GetFileName(target.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        if (!leafName.StartsWith(SyntheticIdPrefix, StringComparison.Ordinal))
            throw new DeployException($"Refusing to deploy: target leaf '{leafName}' is missing the synthetic '{SyntheticIdPrefix}' prefix");
        if (string.Equals(leafName, realWorkshopId, StringComparison.Ordinal))
            throw new DeployException($"Refusing to deploy: target leaf equals the real workshop ID '{realWorkshopId}'");
    }

    internal static void ValidateZipEntries(ZipArchive archive)
    {
        if (archive.Entries.Count > MaxEntryCount)
            throw new DeployException($"Zip has {archive.Entries.Count} entries — over the {MaxEntryCount} cap");
        long total = 0;
        foreach (var entry in archive.Entries)
        {
            total += entry.Length;
            if (total > MaxUncompressedBytes)
                throw new DeployException($"Zip uncompressed total exceeds {MaxUncompressedBytes:N0} bytes — refusing as a zip-bomb safeguard");

            if (string.IsNullOrEmpty(entry.Name)) continue;
            var full = entry.FullName.Replace('\\', '/');
            if (full.Contains("..", StringComparison.Ordinal))
                throw new DeployException($"Zip entry '{entry.FullName}' contains '..' — refusing as path traversal");
            if (Path.IsPathRooted(full))
                throw new DeployException($"Zip entry '{entry.FullName}' is an absolute path — refusing");
            var safeName = Path.GetFileName(full);
            if (string.IsNullOrEmpty(safeName))
                throw new DeployException($"Zip entry '{entry.FullName}' has no usable filename");
            foreach (var c in Path.GetInvalidFileNameChars())
                if (safeName.Contains(c))
                    throw new DeployException($"Zip entry '{entry.FullName}' contains invalid filename character");
        }
    }

    internal static void ValidateRealWorkshopId(string realWorkshopId)
    {
        if (string.IsNullOrWhiteSpace(realWorkshopId))
            throw new DeployException("Workshop ID is required");
        if (!WorkshopIdPattern.IsMatch(realWorkshopId))
            throw new DeployException($"Workshop ID '{realWorkshopId}' is not a valid Steam Workshop ID (digits only)");
        if (realWorkshopId.StartsWith(SyntheticIdPrefix, StringComparison.Ordinal) && realWorkshopId.Length >= 11)
            throw new DeployException($"Workshop ID '{realWorkshopId}' looks like a synthetic ID — pass the REAL workshop ID, the tool synthesizes the local one");
    }
}

public sealed class DeployException : Exception
{
    public DeployException(string message) : base(message) { }
}
