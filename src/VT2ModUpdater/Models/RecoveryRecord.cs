namespace VT2ModUpdater.Models;

/// <summary>
/// Strictly validated schema-1 recovery metadata emitted by the
/// vermintide-2-tweaker release producer.
/// </summary>
public sealed record RecoveryRecord(
    int Schema,
    RecoveryRelease Release,
    string ModFolder,
    string ModId,
    string WorkshopId,
    string Version,
    RecoveryAsset Asset,
    RecoverySource Source,
    RecoveryBuilder Builder,
    string BundleAuthority,
    RecoveryAuthorityProof AuthorityProof,
    string RootBundle,
    RecoveryDescriptor Descriptor,
    RecoveryOutput Output,
    RecoveryBuildReceipt BuildReceipt);

public sealed record RecoveryRelease(string Repository, string Tag);

public sealed record RecoveryAsset(string Filename, long Length, string Sha256);

public sealed record RecoverySource(
    string Commit,
    string State,
    string ItemCfgSha256,
    string ItemCfgGitBlob);

public sealed record RecoveryBuilder(string Name, string Version);

public sealed record RecoveryAuthorityProof(
    string ByteSource,
    string InventoryGitBlob,
    string IgnoreGitBlob);

public sealed record RecoveryDescriptor(string Filename, string Sha256, string GitBlob);

public sealed record RecoveryOutput(
    string Algorithm,
    string FingerprintSha256,
    IReadOnlyList<RecoveryOutputFile> Files);

public sealed record RecoveryOutputFile(
    string Filename,
    long Length,
    string Sha256,
    string GitBlob);

public sealed record RecoveryBuildReceipt(
    string Path,
    int Schema,
    string GitBlob,
    string Sha256,
    string SourceAlgorithm,
    string SourceFingerprintSha256,
    string RootBundle,
    string DescriptorName,
    string DescriptorSha256,
    string OutputAlgorithm,
    string OutputFingerprintSha256,
    string BuilderName,
    string BuilderVersion,
    RecoveryNormalizationPolicy NormalizationPolicy);

public sealed record RecoveryNormalizationPolicy(
    string Algorithm,
    string FingerprintSha256,
    IReadOnlyList<RecoveryExcludedOutput> ExcludedOutputs);

public sealed record RecoveryExcludedOutput(string Filename, string Sha256);

/// <summary>
/// Parent manifest fields which a recovery child duplicates and therefore must
/// match exactly. The containing daily manifest tag is intentionally absent:
/// carried rows retain the original asset tag in <see cref="RecoveryRelease.Tag"/>.
/// </summary>
public sealed record RecoveryManifestBinding(
    string ModId,
    string WorkshopId,
    string Version,
    string AssetFilename,
    string AssetSha256,
    string SourceCommit,
    string SourceState,
    string BuilderName,
    string BuilderVersion,
    string BundleAuthority,
    string RootBundle,
    string DescriptorName,
    IReadOnlyList<RecoveryManifestBundleFile> BundleFiles);

public sealed record RecoveryManifestBundleFile(string Filename, string Sha256);

public sealed record ValidatedRecoveryRecord(
    RecoveryRecord Record,
    string SemanticEquivalenceAlgorithm,
    string SemanticEquivalenceSha256);
