using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using VT2ModUpdater.Models;
using VT2ModUpdater.Services;

if (args.Length != 5 || args[0] != "install") return 64;
var root = Path.GetFullPath(args[1]);
var target = Path.GetFullPath(args[2]);
var stagePath = Path.GetFullPath(args[3]);
var requested = args[4];
var artifact = Artifact();
var outputs = Directory.EnumerateFiles(stagePath)
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
using var stage = new SourceExactZipStage(
    stagePath,
    target,
    artifact,
    artifact.AssetSha256,
    outputs);

var rollbackInjected = false;
var transaction = new SourceExactDirectoryTransaction(
    lockTimeout: TimeSpan.FromSeconds(2),
    checkpoint: point =>
    {
        if (requested == "hold-lock" && point == "lock-acquired")
        {
            File.WriteAllText(Path.Combine(root, "lock.ready"), "ready");
            Thread.Sleep(TimeSpan.FromMinutes(2));
            return;
        }
        if (requested.StartsWith("rollback:", StringComparison.Ordinal) &&
            point == "prior-moved" && !rollbackInjected)
        {
            rollbackInjected = true;
            throw new IOException("fixture requests rollback cleanup");
        }
        var deathPoint = requested.StartsWith("rollback:", StringComparison.Ordinal)
            ? requested["rollback:".Length..]
            : requested;
        if (point == deathPoint)
            TerminateProcess(GetCurrentProcess(), 197);
    });

try
{
    _ = transaction.Install(stage, artifact);
    return requested == "complete" ? 0 : 65;
}
catch when (requested.StartsWith("rollback:", StringComparison.Ordinal))
{
    return 66;
}

static SourceExactRecoveryArtifact Artifact()
{
    var fixture = Path.Combine(
        AppContext.BaseDirectory,
        "Fixtures",
        "RecoveryRecords",
        "valid-tracked.json");
    var json = JsonNode.Parse(File.ReadAllText(fixture))!.AsObject();
    var sourceCommit = json["source"]!["commit"]!.GetValue<string>();
    var assetSha = json["asset"]!["sha256"]!.GetValue<string>();
    var proof = RecoveryRecordContract.ParseAndValidate(
        json.ToJsonString(),
        Binding(assetSha, sourceCommit));
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

static RecoveryManifestBinding Binding(string assetSha256, string sourceCommit) => new(
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
        new RecoveryManifestBundleFile(
            "modx.mod",
            "6db3ae2ce8ed0d57f22fb35a5beaa8cb0ec35ec9d560b829e582dd4d63ea78f3")
    }));

static string Sha256(byte[] bytes) =>
    Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

[DllImport("kernel32.dll")]
static extern IntPtr GetCurrentProcess();

[DllImport("kernel32.dll")]
[return: MarshalAs(UnmanagedType.Bool)]
static extern bool TerminateProcess(IntPtr process, uint exitCode);
