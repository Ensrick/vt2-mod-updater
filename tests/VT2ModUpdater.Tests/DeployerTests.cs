using System.IO;
using System.IO.Compression;
using VT2ModUpdater.Services;

namespace VT2ModUpdater.Tests;

public class DeployerTests
{
    [Fact]
    public void SyntheticIdFor_PrependsTen()
    {
        Assert.Equal("103712929235", Deployer.SyntheticIdFor("3712929235"));
        Assert.Equal("103712896117", Deployer.SyntheticIdFor("3712896117"));
        Assert.Equal("10123", Deployer.SyntheticIdFor("123"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("abc")]
    [InlineData("123abc")]
    [InlineData("12.34")]
    [InlineData("-1")]
    [InlineData("12 34")]
    public void SyntheticIdFor_RejectsNonNumericIds(string bad)
    {
        Assert.Throws<DeployException>(() => Deployer.SyntheticIdFor(bad));
    }

    [Fact]
    public void SyntheticIdFor_RejectsAlreadySyntheticIds()
    {
        // Defense against a caller accidentally passing the synthetic ID back in.
        var ex = Assert.Throws<DeployException>(() => Deployer.SyntheticIdFor("103712929235"));
        Assert.Contains("synthetic", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void GetRealFolder_ReturnsWorkshopSlashId()
    {
        var p = Deployer.GetRealFolder(@"C:\Steam\workshop\content\552500", "3712929235");
        Assert.EndsWith(@"552500\3712929235", p);
    }

    [Fact]
    public void GetSyntheticFolder_ReturnsWorkshopSlashSyntheticId()
    {
        var p = Deployer.GetSyntheticFolder(@"C:\Steam\workshop\content\552500", "3712929235");
        Assert.EndsWith(@"552500\103712929235", p);
    }

    [Fact]
    public void AssertTargetIsSynthetic_AcceptsSyntheticTarget()
    {
        var root = @"C:\Steam\workshop\content\552500";
        Deployer.AssertTargetIsSynthetic(Path.Combine(root, "103712929235"), root, "3712929235");
    }

    [Fact]
    public void AssertTargetIsSynthetic_RejectsRealIdTarget_v0_1_0_RegressionGuard()
    {
        // v0.1.0 wrote into the real workshop folder. This is the regression guard.
        var root = @"C:\Steam\workshop\content\552500";
        var target = Path.Combine(root, "3712929235");
        var ex = Assert.Throws<DeployException>(() =>
            Deployer.AssertTargetIsSynthetic(target, root, "3712929235"));
        Assert.Contains("synthetic", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AssertTargetIsSynthetic_RejectsArbitraryTarget()
    {
        var root = @"C:\Steam\workshop\content\552500";
        Assert.Throws<DeployException>(() =>
            Deployer.AssertTargetIsSynthetic(@"C:\Windows\System32", root, "3712929235"));
    }

    [Fact]
    public void ValidateZipEntries_AcceptsCleanZip()
    {
        var bytes = MakeZip(("mod.bin", "x"), ("mod.cer", "y"));
        using var ms = new MemoryStream(bytes);
        using var archive = new ZipArchive(ms, ZipArchiveMode.Read);
        Deployer.ValidateZipEntries(archive); // does not throw
    }

    [Fact]
    public void ValidateZipEntries_RejectsPathTraversal()
    {
        var bytes = MakeZip(("../../etc/passwd", "x"));
        using var ms = new MemoryStream(bytes);
        using var archive = new ZipArchive(ms, ZipArchiveMode.Read);
        var ex = Assert.Throws<DeployException>(() => Deployer.ValidateZipEntries(archive));
        Assert.Contains("..", ex.Message);
    }

    [Fact]
    public void ValidateZipEntries_RejectsAbsolutePath()
    {
        var bytes = MakeZip((@"C:\windows\evil.dll", "x"));
        using var ms = new MemoryStream(bytes);
        using var archive = new ZipArchive(ms, ZipArchiveMode.Read);
        Assert.Throws<DeployException>(() => Deployer.ValidateZipEntries(archive));
    }

    [Fact]
    public void DeployZipBytes_HappyPath_WritesFilesAndSidecar()
    {
        using var tmp = new TempWorkshopRoot();
        var bytes = MakeZip(("mod.bin", "binary-data"), ("career_tweaker.mod", "mod-data"));
        Deployer.DeployZipBytes(bytes, tmp.Root, "3716286199", "0.3.2-dev");

        var target = Path.Combine(tmp.Root, "103716286199");
        Assert.True(Directory.Exists(target));
        Assert.Equal("binary-data", File.ReadAllText(Path.Combine(target, "mod.bin")));
        Assert.Equal("mod-data", File.ReadAllText(Path.Combine(target, "career_tweaker.mod")));
        Assert.Equal("0.3.2-dev", File.ReadAllText(Path.Combine(target, Deployer.VersionSidecarFilename)));

        // Real folder must NOT be created.
        Assert.False(Directory.Exists(Path.Combine(tmp.Root, "3716286199")));
    }

    [Fact]
    public void DeployZipBytes_RejectsRealIdInWorkshopId()
    {
        using var tmp = new TempWorkshopRoot();
        var bytes = MakeZip(("mod.bin", "x"));
        // Passing the synthetic ID as the realWorkshopId should be rejected.
        Assert.Throws<DeployException>(() => Deployer.DeployZipBytes(bytes, tmp.Root, "103716286199", "v"));
    }

    [Fact]
    public void DeployZipBytes_RejectsEmptyZip()
    {
        using var tmp = new TempWorkshopRoot();
        Assert.Throws<DeployException>(() => Deployer.DeployZipBytes(Array.Empty<byte>(), tmp.Root, "3716286199", "v"));
    }

    [Fact]
    public void DeployZipBytes_RejectsMissingWorkshopRoot()
    {
        var bytes = MakeZip(("mod.bin", "x"));
        Assert.Throws<DeployException>(() => Deployer.DeployZipBytes(bytes, @"C:\does\not\exist\anywhere", "3716286199", "v"));
    }

    [Fact]
    public void DeployZipBytes_RejectsTraversalEntries()
    {
        using var tmp = new TempWorkshopRoot();
        var bytes = MakeZip(("../escape.dat", "x"));
        Assert.Throws<DeployException>(() => Deployer.DeployZipBytes(bytes, tmp.Root, "3716286199", "v"));

        // Nothing was extracted.
        var target = Path.Combine(tmp.Root, "103716286199");
        if (Directory.Exists(target))
            Assert.Empty(Directory.GetFiles(target));
    }

    [Fact]
    public void ReadInstalledVersion_ReturnsNullIfMissing()
    {
        using var tmp = new TempWorkshopRoot();
        Assert.Null(Deployer.ReadInstalledVersion(tmp.Root, "3716286199"));
    }

    [Fact]
    public void ReadInstalledVersion_RoundTripsAfterDeploy()
    {
        using var tmp = new TempWorkshopRoot();
        var bytes = MakeZip(("mod.bin", "x"));
        Deployer.DeployZipBytes(bytes, tmp.Root, "3716286199", "0.3.2-dev");
        Assert.Equal("0.3.2-dev", Deployer.ReadInstalledVersion(tmp.Root, "3716286199"));
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
            Root = Path.Combine(Path.GetTempPath(), "vt2updater_tests_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Root);
        }
        public void Dispose()
        {
            try { if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true); } catch { /* best-effort */ }
        }
    }
}
