using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using VT2ModUpdater.Models;
using VT2ModUpdater.Services;

namespace VT2ModUpdater.Tests;

internal static class SourceExactTransactionTestFixture
{
    internal const string DescriptorSha256 =
        "6db3ae2ce8ed0d57f22fb35a5beaa8cb0ec35ec9d560b829e582dd4d63ea78f3";

    internal static SourceExactRecoveryArtifact Artifact()
    {
        var json = JsonNode.Parse(RecoveryFixture("valid-tracked.json"))!.AsObject();
        var sourceCommit = json["source"]!["commit"]!.GetValue<string>();
        var proof = RecoveryRecordContract.ParseAndValidate(
            json.ToJsonString(),
            Binding(json["asset"]!["sha256"]!.GetValue<string>(), sourceCommit));
        return new SourceExactRecoveryArtifact(
            RecoveryRecordContract.Repository,
            proof.Record.Release.Tag,
            100,
            "mods-container-2026-08-28",
            DateTimeOffset.Parse("2026-08-28T12:00:00Z"),
            200,
            proof.Record.Asset.Filename,
            proof.Record.Asset.Length,
            proof.Record.Asset.Sha256,
            "https://release-assets.githubusercontent.com/fixture",
            proof,
            1,
            1);
    }

    internal static SourceExactZipStage CreateStage(
        string root,
        string target,
        string? archiveSha256 = null)
    {
        var path = Path.Combine(
            root, ".vt2-source-exact-stage-" + Guid.NewGuid().ToString("N"));
        WriteRawStage(path);
        var artifact = Artifact();
        return new SourceExactZipStage(
            path,
            target,
            artifact,
            archiveSha256 ?? artifact.AssetSha256,
            Outputs(path));
    }

    internal static void WriteRawStage(string path)
    {
        Directory.CreateDirectory(path);
        var files = StageFiles();
        foreach (var row in files)
            File.WriteAllBytes(Path.Combine(path, row.Key), row.Value);
    }

    internal static IReadOnlyList<SourceExactStagedOutput> Outputs(string stagePath) =>
        Directory.EnumerateFiles(stagePath)
            .Select(path => new
            {
                Name = Path.GetFileName(path),
                Bytes = File.ReadAllBytes(path)
            })
            .Where(row => row.Name != SourceExactZipStager.VersionMarkerFilename)
            .OrderBy(row => row.Name, StringComparer.Ordinal)
            .Select(row => new SourceExactStagedOutput(
                row.Name,
                row.Bytes.LongLength,
                Sha256(row.Bytes)))
            .ToArray();

    internal static void WritePriorTarget(string target)
    {
        Directory.CreateDirectory(target);
        File.WriteAllBytes(Path.Combine(target, "old.mod_bundle"), new byte[] { 9, 8, 7 });
        File.WriteAllText(Path.Combine(target, "vt2updater_version.txt"), "old");
    }

    internal static string HarnessPath()
    {
        var cursor = new DirectoryInfo(AppContext.BaseDirectory);
        while (cursor is not null && cursor.Name != "VT2ModUpdater.Tests")
            cursor = cursor.Parent;
        var testsProject = cursor ??
            throw new DirectoryNotFoundException("cannot locate the tests project root");
        var configuration = AppContext.BaseDirectory.Split(
                Path.DirectorySeparatorChar,
                StringSplitOptions.RemoveEmptyEntries)
            .Single(part => part is "Debug" or "Release");
        var frameworkRoot = Path.Combine(
            testsProject.Parent!.FullName,
            "VT2ModUpdater.SourceExactCrashHarness",
            "bin",
            configuration,
            "net9.0-windows");
        var ridPath = Path.Combine(frameworkRoot, "win-x64",
            "VT2ModUpdater.SourceExactCrashHarness.exe");
        return File.Exists(ridPath)
            ? ridPath
            : Path.Combine(frameworkRoot, "VT2ModUpdater.SourceExactCrashHarness.exe");
    }

    internal static string Sha256(byte[] bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    internal static string Snapshot(string path) => !Directory.Exists(path)
        ? "absent"
        : string.Join("\n", Directory.EnumerateFiles(path)
            .OrderBy(file => Path.GetFileName(file), StringComparer.Ordinal)
            .Select(file => Path.GetFileName(file) + ":" +
                Sha256(File.ReadAllBytes(file))));

    private static Dictionary<string, byte[]> StageFiles() => new(StringComparer.Ordinal)
    {
        ["0123456789abcdef.mod_bundle"] = new byte[] { 1, 3, 3, 7, 9, 11, 13, 17 },
        ["fedcba9876543210.mod_bundle"] = new byte[] { 2, 4, 6, 8, 10, 12, 14, 16 },
        ["modx.mod"] = Encoding.ASCII.GetBytes("fixture descriptor\n"),
        [SourceExactZipStager.VersionMarkerFilename] =
            Encoding.ASCII.GetBytes("1.2.3-dev")
    };

    private static RecoveryManifestBinding Binding(
        string assetSha256,
        string sourceCommit) => new(
            "mx", "1234567890", "1.2.3-dev", "mx.zip", assetSha256, sourceCommit,
            "clean", "VMBLauncher", "9.8.7+fixture", "tracked",
            "0123456789abcdef.mod_bundle", "modx.mod",
            Array.AsReadOnly(new[]
            {
                new RecoveryManifestBundleFile(
                    "0123456789abcdef.mod_bundle",
                    "57f4bc8fc7f9a9271afe6d3d0aed6afc675f06b6f6fb738b838d4f53da60f5c6"),
                new RecoveryManifestBundleFile(
                    "fedcba9876543210.mod_bundle",
                    "92b80db12ef207bda13fb28ade13297e316259f357d339c2fc84393854402cb5"),
                new RecoveryManifestBundleFile("modx.mod", DescriptorSha256)
            }));

    private static string RecoveryFixture(string name) => File.ReadAllText(Path.Combine(
        AppContext.BaseDirectory, "Fixtures", "RecoveryRecords", name));
}
