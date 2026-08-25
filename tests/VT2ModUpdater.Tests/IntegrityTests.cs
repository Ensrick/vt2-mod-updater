using System.IO;
using System.IO.Compression;
using System.Text;
using VT2ModUpdater.Services;

namespace VT2ModUpdater.Tests;

public class IntegrityTests
{
    [Fact]
    public void ComputeSha256Hex_KnownVector_EmptyInput()
    {
        // SHA-256 of zero-length input — canonical NIST test vector.
        Assert.Equal(
            "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855",
            Deployer.ComputeSha256Hex(Array.Empty<byte>()));
    }

    [Fact]
    public void ComputeSha256Hex_KnownVector_AbcInput()
    {
        // SHA-256 of "abc" — canonical NIST test vector.
        Assert.Equal(
            "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad",
            Deployer.ComputeSha256Hex(Encoding.ASCII.GetBytes("abc")));
    }

    [Fact]
    public void ComputeSha256Hex_OutputIsLowercaseHex()
    {
        var hash = Deployer.ComputeSha256Hex(new byte[] { 1, 2, 3, 4, 5 });
        Assert.Equal(64, hash.Length);
        Assert.All(hash, c => Assert.True(char.IsDigit(c) || (c >= 'a' && c <= 'f'),
            $"non-lowercase-hex character '{c}' in '{hash}'"));
    }

    [Fact]
    public void VerifyBundleIntegrity_MatchingHash_ReturnsMatched()
    {
        var bytes = MakeZip(("mod.bin", "x"));
        var expected = Deployer.ComputeSha256Hex(bytes);

        var result = Deployer.VerifyBundleIntegrity(bytes, expected);
        Assert.Equal(IntegrityResult.Matched, result.Result);
        Assert.Equal(expected, result.ComputedSha256);
        Assert.False(result.ShouldRefuseExtract);
    }

    [Fact]
    public void VerifyBundleIntegrity_TamperedBundle_ReturnsMismatch()
    {
        // Original bundle + expected hash.
        var bytes = MakeZip(("mod.bin", "x"));
        var expected = Deployer.ComputeSha256Hex(bytes);

        // Tamper: flip a byte mid-buffer.
        var tampered = (byte[])bytes.Clone();
        tampered[10] ^= 0xFF;

        var result = Deployer.VerifyBundleIntegrity(tampered, expected);
        Assert.Equal(IntegrityResult.Mismatch, result.Result);
        Assert.NotEqual(expected, result.ComputedSha256);
        Assert.True(result.ShouldRefuseExtract);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void VerifyBundleIntegrity_NoExpectedHash_SkipsCleanly(string? expected)
    {
        var bytes = MakeZip(("mod.bin", "x"));
        var result = Deployer.VerifyBundleIntegrity(bytes, expected);
        Assert.Equal(IntegrityResult.SkippedNoExpectedHash, result.Result);
        Assert.False(result.ShouldRefuseExtract);
    }

    [Theory]
    [InlineData("notahex")]
    [InlineData("ABCDEF")]                                                              // too short
    [InlineData("zzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzz")]    // non-hex chars
    [InlineData("e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855xx")]  // too long
    public void VerifyBundleIntegrity_MalformedExpected_Refuses(string malformed)
    {
        var bytes = MakeZip(("mod.bin", "x"));
        var result = Deployer.VerifyBundleIntegrity(bytes, malformed);
        Assert.Equal(IntegrityResult.MalformedExpected, result.Result);
        Assert.True(result.ShouldRefuseExtract);
    }

    [Fact]
    public void DeployZipBytes_MismatchedExpectedHash_DoesNotCreateTarget()
    {
        using var tmp = new TempWorkshopRoot();
        var bytes = MakeZip(("mod.bin", "payload"));
        var wrong = Deployer.ComputeSha256Hex(MakeZip(("mod.bin", "different")));

        var error = Assert.Throws<DeployException>(() =>
            Deployer.DeployZipBytes(bytes, tmp.Root, "3712929235", "1.0.0", wrong));

        Assert.Contains("integrity mismatch", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(Directory.Exists(Path.Combine(tmp.Root, "103712929235")));
    }

    [Fact]
    public void DeployZipBytes_MalformedExpectedHash_DoesNotCreateTarget()
    {
        using var tmp = new TempWorkshopRoot();
        var bytes = MakeZip(("mod.bin", "payload"));

        var error = Assert.Throws<DeployException>(() =>
            Deployer.DeployZipBytes(bytes, tmp.Root, "3712929235", "1.0.0", "not-a-hash"));

        Assert.Contains("malformed", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(Directory.Exists(Path.Combine(tmp.Root, "103712929235")));
    }

    [Fact]
    public void VerifyBundleIntegrity_UppercaseExpected_NormalizesAndMatches()
    {
        // Producer always emits lowercase, but a defensive uppercase pass-through should still match.
        var bytes = MakeZip(("mod.bin", "x"));
        var expectedLower = Deployer.ComputeSha256Hex(bytes);
        var expectedUpper = expectedLower.ToUpperInvariant();

        var result = Deployer.VerifyBundleIntegrity(bytes, expectedUpper);
        Assert.Equal(IntegrityResult.Matched, result.Result);
    }

    [Fact]
    public void TamperTest_OneBitFlipDetected_E2E()
    {
        // End-to-end: producer hashes a real zip, consumer detects a one-byte tamper.
        // Mirrors the manual tamper test from Issue #30 without needing a live GitHub release.
        var pristine = MakeZip(
            ("ct.bundle", "binary-data-pretending-to-be-a-bundle"),
            ("ct.mod", "mod-script-content"),
            ("vt2updater_version.txt", "0.7.80-alpha"));
        var manifestSha = Deployer.ComputeSha256Hex(pristine);

        // One byte flipped — the kind of corruption a flaky CDN or partial transfer produces.
        var corrupted = (byte[])pristine.Clone();
        corrupted[100] = (byte)(corrupted[100] ^ 0x01);

        var result = Deployer.VerifyBundleIntegrity(corrupted, manifestSha);
        Assert.Equal(IntegrityResult.Mismatch, result.Result);
        Assert.True(result.ShouldRefuseExtract,
            "single-bit tamper must be detected and refused — this is the load-bearing assertion for Issue #30");
    }

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
        public string Root { get; } = Path.Combine(
            Path.GetTempPath(),
            "vt2updater_integrity_tests_" + Guid.NewGuid().ToString("N"));

        public TempWorkshopRoot() => Directory.CreateDirectory(Root);

        public void Dispose()
        {
            try { if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true); }
            catch { /* best-effort test cleanup */ }
        }
    }
}
