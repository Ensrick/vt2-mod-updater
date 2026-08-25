using System.IO;
using System.IO.Compression;
using VT2ModUpdater.Models;
using VT2ModUpdater.Services;

namespace VT2ModUpdater.Tests;

/// <summary>
/// Coverage for Issue #32 — post-install verification of already-deployed bundles.
/// The flow is: <c>DeployZipBytes</c> stashes a <c>.vt2updater_sha256.txt</c> sidecar
/// containing the manifest zip hash and a deterministic Merkle-style hash of the
/// extracted file contents. Later, <c>VerifyInstalled</c> reads the sidecar, recomputes
/// the installed-files hash, and compares both stashed values to the latest manifest
/// to classify the row into OK / OUT_OF_DATE / TAMPERED / NO_SIDECAR / NOT_INSTALLED.
/// </summary>
public class InstalledVerificationTests
{
    [Theory]
    [InlineData(VerifyState.OutOfDate)]
    [InlineData(VerifyState.Tampered)]
    [InlineData(VerifyState.NoSidecar)]
    [InlineData(VerifyState.NotInstalled)]
    public void VerificationFailure_EnablesRepairAtSameVersion(VerifyState state)
    {
        var row = new ModRow(new ManifestEntry { Version = "1.0.0" })
        {
            InstalledVersion = "1.0.0",
            VerifyState = state,
        };

        Assert.True(row.CanUpdate);
    }

    [Fact]
    public void VerifiedSameVersion_RemainsUpToDate()
    {
        var row = new ModRow(new ManifestEntry { Version = "1.0.0" })
        {
            InstalledVersion = "1.0.0",
            VerifyState = VerifyState.Ok,
        };

        Assert.False(row.CanUpdate);
    }

    [Fact]
    public void ComputeInstalledFilesHash_IsDeterministicAcrossOrderingOfFiles()
    {
        // Two folders with identical files written in different create orders must
        // hash to the same digest. The Merkle helper sorts by filename before feeding
        // the running SHA, so write-order shouldn't matter.
        using var a = new TempFolder();
        using var b = new TempFolder();

        File.WriteAllText(Path.Combine(a.Path, "ct.bundle"), "alpha");
        File.WriteAllText(Path.Combine(a.Path, "ct.mod"), "bravo");

        // Write in opposite order in b.
        File.WriteAllText(Path.Combine(b.Path, "ct.mod"), "bravo");
        File.WriteAllText(Path.Combine(b.Path, "ct.bundle"), "alpha");

        Assert.Equal(
            Deployer.ComputeInstalledFilesHash(a.Path),
            Deployer.ComputeInstalledFilesHash(b.Path));
    }

    [Fact]
    public void ComputeInstalledFilesHash_ExcludesSidecars()
    {
        using var folder = new TempFolder();
        File.WriteAllText(Path.Combine(folder.Path, "ct.bundle"), "payload");
        var withoutSidecar = Deployer.ComputeInstalledFilesHash(folder.Path);

        // Both sidecars must NOT affect the result — they're metadata, not payload.
        File.WriteAllText(Path.Combine(folder.Path, Deployer.VersionSidecarFilename), "1.0.0");
        File.WriteAllText(Path.Combine(folder.Path, Deployer.IntegritySidecarFilename), "manifest_sha256=x\ninstalled_files_sha256=y\n");

        Assert.Equal(withoutSidecar, Deployer.ComputeInstalledFilesHash(folder.Path));
    }

    [Fact]
    public void ComputeInstalledFilesHash_OneByteChange_ChangesDigest()
    {
        using var folder = new TempFolder();
        File.WriteAllText(Path.Combine(folder.Path, "ct.bundle"), "payload");
        var before = Deployer.ComputeInstalledFilesHash(folder.Path);

        // Mutate one byte.
        File.WriteAllText(Path.Combine(folder.Path, "ct.bundle"), "payloaD");
        var after = Deployer.ComputeInstalledFilesHash(folder.Path);

        Assert.NotEqual(before, after);
    }

    [Fact]
    public void DeployZipBytes_WritesIntegritySidecarWithBothHashes()
    {
        using var tmp = new TempWorkshopRoot();
        var bytes = MakeZip(("ct.bundle", "payload"), ("ct.mod", "scripts"));
        var manifestSha = Deployer.ComputeSha256Hex(bytes);
        Deployer.DeployZipBytes(bytes, tmp.Root, "3712929235", "0.7.80-alpha", manifestSha);

        var target = Path.Combine(tmp.Root, "103712929235");
        var sidecarPath = Path.Combine(target, Deployer.IntegritySidecarFilename);
        Assert.True(File.Exists(sidecarPath), "integrity sidecar must be written");

        var parsed = Deployer.ReadIntegritySidecar(target);
        Assert.NotNull(parsed);
        Assert.Equal(manifestSha, parsed!.Value.ManifestSha256);
        Assert.Equal(Deployer.ComputeInstalledFilesHash(target), parsed.Value.InstalledFilesSha256);
    }

    [Fact]
    public void DeployZipBytes_WithoutManifestSha_StillWritesInstalledFilesHash()
    {
        // Older release without sha256 in manifest — sidecar still written so future
        // verify passes can at least detect TAMPERED.
        using var tmp = new TempWorkshopRoot();
        var bytes = MakeZip(("ct.bundle", "payload"));
        Deployer.DeployZipBytes(bytes, tmp.Root, "3712929235", "0.7.80-alpha", expectedSha256: null);

        var target = Path.Combine(tmp.Root, "103712929235");
        var parsed = Deployer.ReadIntegritySidecar(target);
        Assert.NotNull(parsed);
        Assert.Null(parsed!.Value.ManifestSha256);
        Assert.False(string.IsNullOrEmpty(parsed.Value.InstalledFilesSha256));
    }

    [Fact]
    public void VerifyInstalled_FreshDeploy_ReturnsOk()
    {
        using var tmp = new TempWorkshopRoot();
        var bytes = MakeZip(("ct.bundle", "payload"));
        var manifestSha = Deployer.ComputeSha256Hex(bytes);
        Deployer.DeployZipBytes(bytes, tmp.Root, "3712929235", "0.7.80-alpha", manifestSha);

        var result = Deployer.VerifyInstalled(tmp.Root, "3712929235", manifestSha);
        Assert.Equal(VerifyState.Ok, result.State);
    }

    /// <summary>
    /// Load-bearing assertion for Issue #32: tampering with an installed file after
    /// deploy must surface as TAMPERED on a verify pass.
    /// </summary>
    [Fact]
    public void TamperedInstalled_OneByteFlip_ReturnsTampered()
    {
        using var tmp = new TempWorkshopRoot();
        var bytes = MakeZip(("ct.bundle", "payload"), ("ct.mod", "scripts"));
        var manifestSha = Deployer.ComputeSha256Hex(bytes);
        Deployer.DeployZipBytes(bytes, tmp.Root, "3712929235", "0.7.80-alpha", manifestSha);

        // Tamper with one byte of the installed bundle.
        var bundlePath = Path.Combine(tmp.Root, "103712929235", "ct.bundle");
        var contents = File.ReadAllBytes(bundlePath);
        contents[0] ^= 0xFF;
        File.WriteAllBytes(bundlePath, contents);

        var result = Deployer.VerifyInstalled(tmp.Root, "3712929235", manifestSha);
        Assert.Equal(VerifyState.Tampered, result.State);
        Assert.NotEqual(result.StashedInstalledFilesSha256, result.ComputedInstalledFilesSha256);
    }

    [Fact]
    public void NewerManifestSha_ReturnsOutOfDate()
    {
        using var tmp = new TempWorkshopRoot();
        var bytes = MakeZip(("ct.bundle", "v1"));
        var oldManifestSha = Deployer.ComputeSha256Hex(bytes);
        Deployer.DeployZipBytes(bytes, tmp.Root, "3712929235", "0.7.80-alpha", oldManifestSha);

        // Manifest moved on to a new bundle hash — installed files untouched, but the
        // remote has a newer payload available.
        var newManifestSha = Deployer.ComputeSha256Hex(MakeZip(("ct.bundle", "v2")));
        var result = Deployer.VerifyInstalled(tmp.Root, "3712929235", newManifestSha);
        Assert.Equal(VerifyState.OutOfDate, result.State);
    }

    [Fact]
    public void LegacySidecarWithoutManifestSha_WhenLatestHasHash_ReturnsOutOfDate()
    {
        using var tmp = new TempWorkshopRoot();
        var bytes = MakeZip(("ct.bundle", "legacy-payload"));

        // A deployment made from a legacy manifest can prove that its installed files
        // are unchanged, but it cannot prove identity with a later hash-bearing
        // manifest. It must remain repairable instead of being labelled verified OK.
        Deployer.DeployZipBytes(
            bytes,
            tmp.Root,
            "3712929235",
            "0.7.80-alpha",
            expectedSha256: null);

        var latestManifestSha = Deployer.ComputeSha256Hex(bytes);
        var result = Deployer.VerifyInstalled(tmp.Root, "3712929235", latestManifestSha);

        Assert.Equal(VerifyState.OutOfDate, result.State);
        Assert.Null(result.StashedManifestSha256);
    }

    [Fact]
    public void NoSidecar_LegacyInstall_ReturnsNoSidecar()
    {
        using var tmp = new TempWorkshopRoot();
        var target = Path.Combine(tmp.Root, "103712929235");
        Directory.CreateDirectory(target);
        File.WriteAllText(Path.Combine(target, "ct.bundle"), "payload");
        File.WriteAllText(Path.Combine(target, Deployer.VersionSidecarFilename), "0.7.80-alpha");
        // Intentionally do NOT write the integrity sidecar.

        var result = Deployer.VerifyInstalled(tmp.Root, "3712929235", "deadbeef");
        Assert.Equal(VerifyState.NoSidecar, result.State);
    }

    [Fact]
    public void FolderMissing_ReturnsNotInstalled()
    {
        using var tmp = new TempWorkshopRoot();
        // Nothing deployed.
        var result = Deployer.VerifyInstalled(tmp.Root, "3712929235", "deadbeef");
        Assert.Equal(VerifyState.NotInstalled, result.State);
    }

    [Fact]
    public void OkState_WithNullLatestManifestSha_DoesNotDegradeToOutOfDate()
    {
        // Older manifest (no sha256). Deploy was done with a sha though — and current
        // files match. Verify should not falsely flag OUT_OF_DATE just because the
        // latest manifest doesn't have a hash to compare against.
        using var tmp = new TempWorkshopRoot();
        var bytes = MakeZip(("ct.bundle", "payload"));
        var manifestSha = Deployer.ComputeSha256Hex(bytes);
        Deployer.DeployZipBytes(bytes, tmp.Root, "3712929235", "0.7.80-alpha", manifestSha);

        var result = Deployer.VerifyInstalled(tmp.Root, "3712929235", latestManifestSha256: null);
        Assert.Equal(VerifyState.Ok, result.State);
    }

    [Fact]
    public void IntegritySidecarParser_TolerantOfWhitespaceAndUppercase()
    {
        using var folder = new TempFolder();
        File.WriteAllText(
            Path.Combine(folder.Path, Deployer.IntegritySidecarFilename),
            "  manifest_sha256 = ABCDEF1234  \n  installed_files_sha256=FEDCBA9876\n");

        var parsed = Deployer.ReadIntegritySidecar(folder.Path);
        Assert.NotNull(parsed);
        Assert.Equal("abcdef1234", parsed!.Value.ManifestSha256);
        Assert.Equal("fedcba9876", parsed.Value.InstalledFilesSha256);
    }

    // ---------------- helpers ----------------

    private static byte[] MakeZip(params (string Name, string Body)[] entries)
    {
        using var ms = new MemoryStream();
        using (var archive = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, body) in entries)
            {
                var entry = archive.CreateEntry(name);
                using var s = entry.Open();
                using var w = new StreamWriter(s);
                w.Write(body);
            }
        }
        return ms.ToArray();
    }

    private sealed class TempWorkshopRoot : IDisposable
    {
        public string Root { get; }
        public TempWorkshopRoot()
        {
            Root = Path.Combine(Path.GetTempPath(), "vt2updater_verify_tests_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Root);
        }
        public void Dispose()
        {
            try { if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true); } catch { /* best-effort */ }
        }
    }

    private sealed class TempFolder : IDisposable
    {
        public string Path { get; }
        public TempFolder()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "vt2updater_merkle_tests_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }
        public void Dispose()
        {
            try { if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true); } catch { /* best-effort */ }
        }
    }
}
