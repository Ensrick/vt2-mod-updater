using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace VT2ModUpdater.Services;

public static class Deployer
{
    public const string VersionSidecarFilename = "vt2updater_version.txt";
    /// <summary>
    /// Sidecar written next to <see cref="VersionSidecarFilename"/> at deploy time. Stores
    /// two values used by the post-install verification flow (Issue #32):
    /// <list type="bullet">
    /// <item><c>manifest_sha256=</c> the SHA-256 of the downloaded zip (i.e. the
    /// manifest entry's <c>sha256</c>). Lets us detect OUT_OF_DATE installs without
    /// trusting the version string — if the producer republishes the same version with
    /// updated bytes, the manifest hash changes and we can spot the drift.</item>
    /// <item><c>installed_files_sha256=</c> a deterministic Merkle-style hash of the
    /// extracted file contents (sorted by filename, sidecar files themselves excluded).
    /// Lets us detect TAMPERED installs where someone edited the bundle on disk after
    /// install. We can't byte-compare against the original zip (re-zipping a directory
    /// won't byte-match Compress-Archive's output) so we hash the extracted layout
    /// instead and stash that at deploy time.</item>
    /// </list>
    /// </summary>
    public const string IntegritySidecarFilename = ".vt2updater_sha256.txt";
    public const string SyntheticIdPrefix = "10";

    /// <summary>50 MB sanity cap on any single mod zip. Real bundles are well under 5 MB.</summary>
    public const long MaxZipBytes = 50L * 1024 * 1024;

    /// <summary>200 MB cap on total uncompressed size — protects against zip bombs.</summary>
    public const long MaxUncompressedBytes = 200L * 1024 * 1024;

    /// <summary>500 entries per zip — bundle has &lt;10 in practice.</summary>
    public const int MaxEntryCount = 500;

    private static readonly Regex WorkshopIdPattern = new("^[0-9]+$", RegexOptions.Compiled);
    private static readonly Regex Sha256HexPattern = new("^[0-9a-f]{64}$", RegexOptions.Compiled);

    /// <summary>
    /// SHA-256 of the supplied bytes as lowercase hex. Matches the producer-side format
    /// emitted by <c>Get-FileHash -Algorithm SHA256</c> after lowercase normalization.
    /// </summary>
    public static string ComputeSha256Hex(byte[] bytes)
    {
        if (bytes is null) throw new ArgumentNullException(nameof(bytes));
        var hash = SHA256.HashData(bytes);
        var sb = new StringBuilder(hash.Length * 2);
        foreach (var b in hash) sb.Append(b.ToString("x2"));
        return sb.ToString();
    }

    /// <summary>
    /// Verifies a downloaded bundle against the manifest's <c>sha256</c> field.
    /// Behaviour:
    /// <list type="bullet">
    /// <item>If <paramref name="expectedSha256"/> is null/empty/whitespace, returns
    /// <see cref="IntegrityResult.SkippedNoExpectedHash"/> — older manifests pre-dating
    /// integrity verification are treated as legacy and pass through. Caller is expected
    /// to debug-log the skip.</item>
    /// <item>If the expected value is not a 64-character hexadecimal string, returns
    /// <see cref="IntegrityResult.MalformedExpected"/>. A producer/schema error cannot
    /// safely authorize extraction, so callers must fail closed.</item>
    /// <item>If the hash matches, returns <see cref="IntegrityResult.Matched"/>.</item>
    /// <item>If the hash differs, returns <see cref="IntegrityResult.Mismatch"/> with
    /// the computed hash on the result so the caller can log both sides.</item>
    /// </list>
    /// Never throws on a mismatch — the caller decides retry / fail-loud behaviour.
    /// </summary>
    public static IntegrityCheck VerifyBundleIntegrity(byte[] bundleBytes, string? expectedSha256)
    {
        if (bundleBytes is null) throw new ArgumentNullException(nameof(bundleBytes));

        if (string.IsNullOrWhiteSpace(expectedSha256))
            return new IntegrityCheck(IntegrityResult.SkippedNoExpectedHash, "", null);

        var normalized = expectedSha256.Trim().ToLowerInvariant();
        if (!Sha256HexPattern.IsMatch(normalized))
            return new IntegrityCheck(IntegrityResult.MalformedExpected, "", normalized);

        var computed = ComputeSha256Hex(bundleBytes);
        return computed == normalized
            ? new IntegrityCheck(IntegrityResult.Matched, computed, normalized)
            : new IntegrityCheck(IntegrityResult.Mismatch, computed, normalized);
    }

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
        => DeployZipBytes(zipBytes, workshopContentRoot, realWorkshopId, version, expectedSha256: null);

    /// <summary>
    /// Same as <see cref="DeployZipBytes(byte[], string, string, string)"/> but also writes
    /// the integrity sidecar (Issue #32) when <paramref name="expectedSha256"/> is supplied.
    /// </summary>
    public static void DeployZipBytes(byte[] zipBytes, string workshopContentRoot, string realWorkshopId, string version, string? expectedSha256)
    {
        if (zipBytes is null || zipBytes.Length == 0)
            throw new DeployException("Empty zip payload");
        if (zipBytes.Length > MaxZipBytes)
            throw new DeployException($"Zip is {zipBytes.Length:N0} bytes — over the {MaxZipBytes:N0} byte sanity cap. Refusing to extract.");
        if (string.IsNullOrWhiteSpace(workshopContentRoot) || !Directory.Exists(workshopContentRoot))
            throw new DeployException($"Workshop content root does not exist: '{workshopContentRoot}'");
        if (string.IsNullOrWhiteSpace(version))
            throw new DeployException("Version string is required");

        // Keep the integrity decision at the filesystem mutation boundary as well as
        // in the view-model's retry loop. A future caller must not be able to bypass
        // verification merely by invoking DeployZipBytes directly.
        if (!string.IsNullOrWhiteSpace(expectedSha256))
        {
            var integrity = VerifyBundleIntegrity(zipBytes, expectedSha256);
            if (integrity.Result == IntegrityResult.MalformedExpected)
                throw new DeployException($"Manifest SHA-256 is malformed: '{expectedSha256}'");
            if (integrity.Result == IntegrityResult.Mismatch)
                throw new DeployException(
                    $"Bundle integrity mismatch. Expected {integrity.ExpectedSha256}, got {integrity.ComputedSha256}.");
        }

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

        // Write the post-install integrity sidecar (Issue #32). The manifest hash is
        // normalized to lowercase; if the producer omitted sha256 we still write an
        // installed_files hash so we can detect tampering even without an OUT_OF_DATE
        // comparison anchor.
        var installedFilesHash = ComputeInstalledFilesHash(target);
        WriteIntegritySidecar(target, NormalizeSha(expectedSha256), installedFilesHash);
    }

    private static string? NormalizeSha(string? sha)
    {
        if (string.IsNullOrWhiteSpace(sha)) return null;
        var n = sha.Trim().ToLowerInvariant();
        return Sha256HexPattern.IsMatch(n) ? n : null;
    }

    /// <summary>
    /// Deterministic content hash of every non-sidecar file under <paramref name="folder"/>,
    /// sorted by filename. For each file we hash <c>length(filename, int32 LE) ||
    /// filename_utf8 || length(content, int64 LE) || content_bytes</c> into a running
    /// SHA-256, then return the final digest as lowercase hex.
    /// Sidecars (<see cref="VersionSidecarFilename"/> and <see cref="IntegritySidecarFilename"/>)
    /// are excluded — they're not part of the bundle payload and the integrity sidecar
    /// is what we're trying to compare against.
    /// </summary>
    public static string ComputeInstalledFilesHash(string folder)
    {
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
            throw new DeployException($"Folder does not exist: '{folder}'");

        var files = Directory.EnumerateFiles(folder, "*", SearchOption.TopDirectoryOnly)
            .Where(p =>
            {
                var name = Path.GetFileName(p);
                return !string.Equals(name, VersionSidecarFilename, StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(name, IntegritySidecarFilename, StringComparison.OrdinalIgnoreCase);
            })
            .OrderBy(p => Path.GetFileName(p), StringComparer.Ordinal)
            .ToList();

        using var sha = SHA256.Create();
        using var ms = new MemoryStream();
        foreach (var path in files)
        {
            var nameBytes = Encoding.UTF8.GetBytes(Path.GetFileName(path));
            var contentBytes = File.ReadAllBytes(path);

            var nameLen = BitConverter.GetBytes(nameBytes.Length);    // int32 LE on x64 .NET
            var contentLen = BitConverter.GetBytes((long)contentBytes.Length);  // int64 LE

            ms.Write(nameLen, 0, nameLen.Length);
            ms.Write(nameBytes, 0, nameBytes.Length);
            ms.Write(contentLen, 0, contentLen.Length);
            ms.Write(contentBytes, 0, contentBytes.Length);
        }
        var digest = sha.ComputeHash(ms.ToArray());
        var sb = new StringBuilder(digest.Length * 2);
        foreach (var b in digest) sb.Append(b.ToString("x2"));
        return sb.ToString();
    }

    /// <summary>
    /// Writes the integrity sidecar at <c>&lt;folder&gt;/<see cref="IntegritySidecarFilename"/></c>.
    /// Format is two <c>key=value</c> lines (LF terminated). Either value may be empty
    /// when the producer omitted a manifest sha256.
    /// </summary>
    public static void WriteIntegritySidecar(string folder, string? manifestSha256, string installedFilesSha256)
    {
        var contents = $"manifest_sha256={manifestSha256 ?? ""}\ninstalled_files_sha256={installedFilesSha256}\n";
        File.WriteAllText(Path.Combine(folder, IntegritySidecarFilename), contents);
    }

    /// <summary>
    /// Reads the integrity sidecar. Returns null when the file is missing — the caller
    /// surfaces that as NO_SIDECAR. Malformed lines are tolerated (any unknown keys are
    /// ignored) but a missing required field surfaces as null on that field of the
    /// returned record.
    /// </summary>
    public static IntegritySidecar? ReadIntegritySidecar(string folder)
    {
        var path = Path.Combine(folder, IntegritySidecarFilename);
        if (!File.Exists(path)) return null;
        string? manifestSha = null;
        string? installedFilesSha = null;
        foreach (var rawLine in File.ReadAllLines(path))
        {
            var line = rawLine.Trim();
            if (line.Length == 0) continue;
            var eq = line.IndexOf('=');
            if (eq <= 0) continue;
            var key = line[..eq].Trim();
            var value = line[(eq + 1)..].Trim();
            if (string.Equals(key, "manifest_sha256", StringComparison.OrdinalIgnoreCase))
                manifestSha = value.Length == 0 ? null : value.ToLowerInvariant();
            else if (string.Equals(key, "installed_files_sha256", StringComparison.OrdinalIgnoreCase))
                installedFilesSha = value.Length == 0 ? null : value.ToLowerInvariant();
        }
        return new IntegritySidecar(manifestSha, installedFilesSha);
    }

    /// <summary>
    /// Classifies the post-install state of one mod (Issue #32). The decision tree:
    /// <list type="bullet">
    /// <item>folder missing → <see cref="VerifyState.NotInstalled"/></item>
    /// <item>folder present but no sidecar → <see cref="VerifyState.NoSidecar"/> (legacy install)</item>
    /// <item>sidecar's <c>installed_files_sha256</c> != current Merkle hash → <see cref="VerifyState.Tampered"/></item>
    /// <item>sidecar's <c>manifest_sha256</c> differs from the latest manifest's sha256 → <see cref="VerifyState.OutOfDate"/></item>
    /// <item>otherwise → <see cref="VerifyState.Ok"/></item>
    /// </list>
    /// When the latest manifest's sha256 is null/blank (older release) we can't decide
    /// OUT_OF_DATE — we degrade to OK on a clean tamper check.
    /// </summary>
    public static InstalledVerification VerifyInstalled(
        string workshopContentRoot,
        string realWorkshopId,
        string? latestManifestSha256)
    {
        var folder = GetSyntheticFolder(workshopContentRoot, realWorkshopId);
        if (!Directory.Exists(folder))
            return new InstalledVerification(VerifyState.NotInstalled, null, null, null);

        var sidecar = ReadIntegritySidecar(folder);
        if (sidecar is null)
            return new InstalledVerification(VerifyState.NoSidecar, null, null, null);

        var currentHash = ComputeInstalledFilesHash(folder);
        if (sidecar.Value.InstalledFilesSha256 is null
            || !string.Equals(sidecar.Value.InstalledFilesSha256, currentHash, StringComparison.Ordinal))
            return new InstalledVerification(
                VerifyState.Tampered,
                sidecar.Value.ManifestSha256,
                currentHash,
                sidecar.Value.InstalledFilesSha256);

        var latestNorm = NormalizeSha(latestManifestSha256);
        if (latestNorm is not null
            && sidecar.Value.ManifestSha256 is not null
            && !string.Equals(sidecar.Value.ManifestSha256, latestNorm, StringComparison.Ordinal))
            return new InstalledVerification(
                VerifyState.OutOfDate,
                sidecar.Value.ManifestSha256,
                currentHash,
                sidecar.Value.InstalledFilesSha256);

        return new InstalledVerification(VerifyState.Ok, sidecar.Value.ManifestSha256, currentHash, sidecar.Value.InstalledFilesSha256);
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

public enum IntegrityResult
{
    /// <summary>Computed hash matches the manifest's <c>sha256</c> field.</summary>
    Matched,
    /// <summary>Computed hash differs from the manifest's <c>sha256</c> — refuse extract.</summary>
    Mismatch,
    /// <summary>Manifest carries no <c>sha256</c> (older release). Caller passes through.</summary>
    SkippedNoExpectedHash,
    /// <summary>Manifest's <c>sha256</c> field is malformed. Extraction must fail closed.</summary>
    MalformedExpected,
}

public readonly record struct IntegrityCheck(IntegrityResult Result, string ComputedSha256, string? ExpectedSha256)
{
    public bool ShouldRefuseExtract => Result is IntegrityResult.Mismatch or IntegrityResult.MalformedExpected;
}

/// <summary>Result of <see cref="Deployer.VerifyInstalled"/> (Issue #32).</summary>
public enum VerifyState
{
    /// <summary>Sidecar present, installed-files hash matches sidecar, manifest hash matches latest manifest.</summary>
    Ok,
    /// <summary>Sidecar present and untouched, but the latest manifest's sha256 has moved on.</summary>
    OutOfDate,
    /// <summary>Sidecar present but the installed-files hash no longer matches what was stashed at deploy time.</summary>
    Tampered,
    /// <summary>Folder exists but no <see cref="Deployer.IntegritySidecarFilename"/> — legacy install pre-dating Issue #32.</summary>
    NoSidecar,
    /// <summary>Synthetic folder does not exist.</summary>
    NotInstalled,
}

/// <summary>On-disk integrity sidecar payload. Either field may be null for legacy / partial writes.</summary>
public readonly record struct IntegritySidecar(string? ManifestSha256, string? InstalledFilesSha256);

/// <summary>
/// Outcome of <see cref="Deployer.VerifyInstalled"/>. <see cref="ComputedInstalledFilesSha256"/>
/// and <see cref="StashedInstalledFilesSha256"/> are populated when meaningful (i.e. the
/// folder + sidecar were readable); both may be null on the NotInstalled / NoSidecar
/// branches.
/// </summary>
public readonly record struct InstalledVerification(
    VerifyState State,
    string? StashedManifestSha256,
    string? ComputedInstalledFilesSha256,
    string? StashedInstalledFilesSha256);
