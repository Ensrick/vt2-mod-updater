using System.Buffers.Binary;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using VT2ModUpdater.Models;

namespace VT2ModUpdater.Services;

/// <summary>
/// Parses and validates only the schema-1 source-exact recovery child emitted by
/// the release producer. It deliberately performs no release lookup, download,
/// extraction, installation, or state mutation.
/// </summary>
public static class RecoveryRecordContract
{
    public const string Repository = "Ensrick/vermintide-2-tweaker";
    public const string OutputFingerprintAlgorithm = "vt2-normalized-bundle-output-set-sha256-v1";
    public const string NormalizationFingerprintAlgorithm = "exact-build-artifact-exclusions-sha256-v1";
    public const string BuildSourceFingerprintAlgorithm = "git-blob-build-byte-map-sha256-v2";
    public const string SemanticEquivalenceAlgorithm = "vt2-recovery-record-semantic-equivalence-sha256-v1";

    internal const int MaxJsonUtf8Bytes = 4 * 1024 * 1024;
    internal const int MaxOutputFiles = 4096;
    internal const int MaxExcludedOutputs = 4096;

    private const int MaxIdentifierBytes = 128;
    private const int MaxVersionBytes = 128;

    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly Regex ReleaseTagPattern = new(
        "\\A[A-Za-z0-9][A-Za-z0-9._-]{0,127}\\z",
        RegexOptions.CultureInvariant);
    private static readonly Regex ModFolderPattern = new(
        "\\A[a-z0-9][a-z0-9_]*\\z",
        RegexOptions.CultureInvariant);
    private static readonly Regex ModIdPattern = new(
        "\\A[A-Za-z0-9][A-Za-z0-9_-]*\\z",
        RegexOptions.CultureInvariant);
    private static readonly Regex RootBundlePattern = new(
        "\\A[0-9a-f]{16}\\.mod_bundle\\z",
        RegexOptions.CultureInvariant);
    private static readonly Regex ZipAssetPattern = new(
        "\\A[A-Za-z0-9][A-Za-z0-9_.-]*\\.zip\\z",
        RegexOptions.CultureInvariant);
    private static readonly Regex LowerSha256Pattern = new(
        "\\A[0-9a-f]{64}\\z",
        RegexOptions.CultureInvariant);
    private static readonly Regex LowerGitBlobPattern = new(
        "\\A[0-9a-f]{40}([0-9a-f]{24})?\\z",
        RegexOptions.CultureInvariant);
    private static readonly Regex PositiveDecimalPattern = new(
        "\\A[1-9][0-9]*\\z",
        RegexOptions.CultureInvariant);

    public static ValidatedRecoveryRecord ParseAndValidate(
        string json,
        RecoveryManifestBinding manifestBinding)
    {
        ArgumentNullException.ThrowIfNull(json);
        ArgumentNullException.ThrowIfNull(manifestBinding);

        if (json.Length > MaxJsonUtf8Bytes || GetUtf8ByteCount(json, "recovery JSON") > MaxJsonUtf8Bytes)
            throw Error($"recovery JSON exceeds the {MaxJsonUtf8Bytes}-byte contract bound");

        ValidateManifestBinding(manifestBinding);

        try
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 32
            });

            var record = ParseRecord(document.RootElement);
            ValidateRecord(record, manifestBinding);
            var digest = ComputeSemanticEquivalenceDigest(record);
            return new ValidatedRecoveryRecord(record, SemanticEquivalenceAlgorithm, digest);
        }
        catch (RecoveryRecordValidationException)
        {
            throw;
        }
        catch (JsonException ex)
        {
            throw Error($"recovery JSON is malformed: {ex.Message}", ex);
        }
        catch (EncoderFallbackException ex)
        {
            throw Error("recovery JSON contains invalid Unicode", ex);
        }
        catch (OverflowException ex)
        {
            throw Error("recovery JSON contains an overflowing numeric value", ex);
        }
    }

    private static RecoveryRecord ParseRecord(JsonElement element)
    {
        var root = StrictObject.Read(element, "recovery",
            "schema", "release", "mod_folder", "mod_id", "workshop_id", "version",
            "asset", "source", "builder", "bundle_authority", "authority_proof",
            "root_bundle", "descriptor", "output", "build_receipt");

        ReadExactInteger(root["schema"], "recovery.schema", 1);

        var releaseObject = StrictObject.Read(root["release"], "recovery.release",
            "repository", "tag");
        var release = new RecoveryRelease(
            ReadString(releaseObject["repository"], "recovery.release.repository", 128),
            ReadString(releaseObject["tag"], "recovery.release.tag", 128));

        var assetObject = StrictObject.Read(root["asset"], "recovery.asset",
            "filename", "length", "sha256");
        var asset = new RecoveryAsset(
            ReadString(assetObject["filename"], "recovery.asset.filename", 256),
            ReadPositiveInt64(assetObject["length"], "recovery.asset.length"),
            ReadString(assetObject["sha256"], "recovery.asset.sha256", 64));

        var sourceObject = StrictObject.Read(root["source"], "recovery.source",
            "commit", "state", "item_cfg_sha256", "item_cfg_git_blob");
        var source = new RecoverySource(
            ReadString(sourceObject["commit"], "recovery.source.commit", 64),
            ReadString(sourceObject["state"], "recovery.source.state", 32),
            ReadString(sourceObject["item_cfg_sha256"], "recovery.source.item_cfg_sha256", 64),
            ReadString(sourceObject["item_cfg_git_blob"], "recovery.source.item_cfg_git_blob", 64));

        var builderObject = StrictObject.Read(root["builder"], "recovery.builder", "name", "version");
        var builder = new RecoveryBuilder(
            ReadString(builderObject["name"], "recovery.builder.name", 64),
            ReadString(builderObject["version"], "recovery.builder.version", MaxVersionBytes));

        var authorityObject = StrictObject.Read(root["authority_proof"], "recovery.authority_proof",
            "byte_source", "inventory_git_blob", "ignore_git_blob");
        var authorityProof = new RecoveryAuthorityProof(
            ReadString(authorityObject["byte_source"], "recovery.authority_proof.byte_source", 64),
            ReadString(authorityObject["inventory_git_blob"], "recovery.authority_proof.inventory_git_blob", 64),
            ReadString(authorityObject["ignore_git_blob"], "recovery.authority_proof.ignore_git_blob", 64));

        var descriptorObject = StrictObject.Read(root["descriptor"], "recovery.descriptor",
            "filename", "sha256", "git_blob");
        var descriptor = new RecoveryDescriptor(
            ReadString(descriptorObject["filename"], "recovery.descriptor.filename", 256),
            ReadString(descriptorObject["sha256"], "recovery.descriptor.sha256", 64),
            ReadString(descriptorObject["git_blob"], "recovery.descriptor.git_blob", 64));

        var output = ParseOutput(root["output"]);
        var buildReceipt = ParseBuildReceipt(root["build_receipt"]);

        return new RecoveryRecord(
            1,
            release,
            ReadString(root["mod_folder"], "recovery.mod_folder", MaxIdentifierBytes),
            ReadString(root["mod_id"], "recovery.mod_id", MaxIdentifierBytes),
            ReadString(root["workshop_id"], "recovery.workshop_id", 20),
            ReadString(root["version"], "recovery.version", MaxVersionBytes),
            asset,
            source,
            builder,
            ReadString(root["bundle_authority"], "recovery.bundle_authority", 16),
            authorityProof,
            ReadString(root["root_bundle"], "recovery.root_bundle", 64),
            descriptor,
            output,
            buildReceipt);
    }

    private static RecoveryOutput ParseOutput(JsonElement element)
    {
        var outputObject = StrictObject.Read(element, "recovery.output",
            "algorithm", "fingerprint_sha256", "files");
        var filesElement = RequireArray(outputObject["files"], "recovery.output.files");
        var count = filesElement.GetArrayLength();
        if (count is < 1 or > MaxOutputFiles)
            throw Error($"recovery.output.files must contain 1..{MaxOutputFiles} entries");

        var files = new RecoveryOutputFile[count];
        var index = 0;
        foreach (var elementRow in filesElement.EnumerateArray())
        {
            var path = $"recovery.output.files[{index}]";
            var row = StrictObject.Read(elementRow, path,
                "filename", "length", "sha256", "git_blob");
            files[index] = new RecoveryOutputFile(
                ReadString(row["filename"], $"{path}.filename", 256),
                ReadPositiveInt64(row["length"], $"{path}.length"),
                ReadString(row["sha256"], $"{path}.sha256", 64),
                ReadString(row["git_blob"], $"{path}.git_blob", 64, allowEmpty: true));
            index++;
        }

        return new RecoveryOutput(
            ReadString(outputObject["algorithm"], "recovery.output.algorithm", 128),
            ReadString(outputObject["fingerprint_sha256"], "recovery.output.fingerprint_sha256", 64),
            Array.AsReadOnly(files));
    }

    private static RecoveryBuildReceipt ParseBuildReceipt(JsonElement element)
    {
        var receiptObject = StrictObject.Read(element, "recovery.build_receipt",
            "path", "schema", "git_blob", "sha256", "source_algorithm",
            "source_fingerprint_sha256", "root_bundle", "descriptor_name",
            "descriptor_sha256", "output_algorithm", "output_fingerprint_sha256",
            "builder_name", "builder_version", "normalization_policy");
        ReadExactInteger(receiptObject["schema"], "recovery.build_receipt.schema", 3);

        var policyObject = StrictObject.Read(
            receiptObject["normalization_policy"],
            "recovery.build_receipt.normalization_policy",
            "algorithm", "fingerprint_sha256", "excluded_outputs");
        var excludedElement = RequireArray(
            policyObject["excluded_outputs"],
            "recovery.build_receipt.normalization_policy.excluded_outputs");
        var count = excludedElement.GetArrayLength();
        if (count > MaxExcludedOutputs)
            throw Error($"recovery normalization exclusions exceed the {MaxExcludedOutputs}-entry contract bound");

        var excluded = new RecoveryExcludedOutput[count];
        var index = 0;
        foreach (var excludedElementRow in excludedElement.EnumerateArray())
        {
            var path = $"recovery.build_receipt.normalization_policy.excluded_outputs[{index}]";
            var row = StrictObject.Read(excludedElementRow, path, "filename", "sha256");
            excluded[index] = new RecoveryExcludedOutput(
                ReadString(row["filename"], $"{path}.filename", 64),
                ReadString(row["sha256"], $"{path}.sha256", 64));
            index++;
        }

        var policy = new RecoveryNormalizationPolicy(
            ReadString(policyObject["algorithm"], "recovery.build_receipt.normalization_policy.algorithm", 128),
            ReadString(policyObject["fingerprint_sha256"], "recovery.build_receipt.normalization_policy.fingerprint_sha256", 64),
            Array.AsReadOnly(excluded));

        return new RecoveryBuildReceipt(
            ReadString(receiptObject["path"], "recovery.build_receipt.path", 256),
            3,
            ReadString(receiptObject["git_blob"], "recovery.build_receipt.git_blob", 64),
            ReadString(receiptObject["sha256"], "recovery.build_receipt.sha256", 64),
            ReadString(receiptObject["source_algorithm"], "recovery.build_receipt.source_algorithm", 128),
            ReadString(receiptObject["source_fingerprint_sha256"], "recovery.build_receipt.source_fingerprint_sha256", 64),
            ReadString(receiptObject["root_bundle"], "recovery.build_receipt.root_bundle", 64),
            ReadString(receiptObject["descriptor_name"], "recovery.build_receipt.descriptor_name", 256),
            ReadString(receiptObject["descriptor_sha256"], "recovery.build_receipt.descriptor_sha256", 64),
            ReadString(receiptObject["output_algorithm"], "recovery.build_receipt.output_algorithm", 128),
            ReadString(receiptObject["output_fingerprint_sha256"], "recovery.build_receipt.output_fingerprint_sha256", 64),
            ReadString(receiptObject["builder_name"], "recovery.build_receipt.builder_name", 64),
            ReadString(receiptObject["builder_version"], "recovery.build_receipt.builder_version", MaxVersionBytes),
            policy);
    }

    private static void ValidateManifestBinding(RecoveryManifestBinding binding)
    {
        ValidateModId(binding.ModId, "manifest.mod_id");
        ValidateWorkshopId(binding.WorkshopId, "manifest.workshop_id");
        ValidateCanonicalText(binding.Version, "manifest.version", MaxVersionBytes);
        ValidateAssetFilename(binding.AssetFilename, binding.ModId, "manifest.asset_filename");
        ValidateSha256(binding.AssetSha256, "manifest.sha256");
        ValidateLowerHex(binding.SourceCommit, 40, "manifest.source_commit");
        RequireEqual(binding.SourceState, "clean", "manifest.source_state");
        RequireEqual(binding.BuilderName, "VMBLauncher", "manifest.builder.name");
        ValidateCanonicalText(binding.BuilderVersion, "manifest.builder.version", MaxVersionBytes);
        ValidateAuthority(binding.BundleAuthority, "manifest.bundle_authority");
        ValidateRootBundle(binding.RootBundle, "manifest.root_bundle");
        ValidateCanonicalLeaf(binding.DescriptorName, "manifest.descriptor_name");
        if (!binding.DescriptorName.EndsWith(".mod", StringComparison.Ordinal))
            throw Error("manifest.descriptor_name must be one exact .mod leaf");
        if (binding.BundleFiles is null)
            throw Error("manifest.bundle_files is null");
        if (binding.BundleFiles.Count is < 1 or > MaxOutputFiles)
            throw Error($"manifest.bundle_files must contain 1..{MaxOutputFiles} entries");

        ValidateUniqueFilenameSet(
            binding.BundleFiles.Select(row => row?.Filename),
            "manifest.bundle_files");
        string? previous = null;
        var descriptorCount = 0;
        var rootCount = 0;
        for (var i = 0; i < binding.BundleFiles.Count; i++)
        {
            var row = binding.BundleFiles[i]
                ?? throw Error($"manifest.bundle_files[{i}] is null");
            ValidateCanonicalLeaf(row.Filename, $"manifest.bundle_files[{i}].filename");
            ValidateOutputFilename(row.Filename, binding.DescriptorName, $"manifest.bundle_files[{i}].filename");
            ValidateSha256(row.Sha256, $"manifest.bundle_files[{i}].sha256");
            RequireOrdinalOrder(row.Filename, ref previous, "manifest.bundle_files");
            if (string.Equals(row.Filename, binding.DescriptorName, StringComparison.Ordinal)) descriptorCount++;
            if (string.Equals(row.Filename, binding.RootBundle, StringComparison.Ordinal)) rootCount++;
        }

        if (descriptorCount != 1)
            throw Error("manifest.bundle_files must contain exactly one declared descriptor");
        if (rootCount != 1)
            throw Error("manifest.bundle_files must contain exactly one declared root bundle");
    }

    private static void ValidateRecord(RecoveryRecord record, RecoveryManifestBinding binding)
    {
        RequireEqual(record.Release.Repository, Repository, "recovery.release.repository");
        if (!ReleaseTagPattern.IsMatch(record.Release.Tag))
            throw Error("recovery.release.tag is not canonical");

        ValidateModFolder(record.ModFolder, "recovery.mod_folder");
        ValidateModId(record.ModId, "recovery.mod_id");
        ValidateWorkshopId(record.WorkshopId, "recovery.workshop_id");
        ValidateCanonicalText(record.Version, "recovery.version", MaxVersionBytes);

        ValidateAssetFilename(record.Asset.Filename, record.ModId, "recovery.asset.filename");
        if (record.Asset.Length <= 0)
            throw Error("recovery.asset.length must be positive");
        ValidateSha256(record.Asset.Sha256, "recovery.asset.sha256");

        ValidateLowerHex(record.Source.Commit, 40, "recovery.source.commit");
        RequireEqual(record.Source.State, "clean", "recovery.source.state");
        ValidateSha256(record.Source.ItemCfgSha256, "recovery.source.item_cfg_sha256");
        ValidateGitBlob(record.Source.ItemCfgGitBlob, "recovery.source.item_cfg_git_blob");

        RequireEqual(record.Builder.Name, "VMBLauncher", "recovery.builder.name");
        ValidateCanonicalText(record.Builder.Version, "recovery.builder.version", MaxVersionBytes);
        ValidateAuthority(record.BundleAuthority, "recovery.bundle_authority");
        var expectedByteSource = record.BundleAuthority == "tracked"
            ? "git_commit_blobs"
            : "materialized_restrictive_handles";
        RequireEqual(record.AuthorityProof.ByteSource, expectedByteSource,
            "recovery.authority_proof.byte_source");
        ValidateGitBlob(record.AuthorityProof.InventoryGitBlob,
            "recovery.authority_proof.inventory_git_blob");
        ValidateGitBlob(record.AuthorityProof.IgnoreGitBlob,
            "recovery.authority_proof.ignore_git_blob");

        ValidateRootBundle(record.RootBundle, "recovery.root_bundle");
        ValidateCanonicalLeaf(record.Descriptor.Filename, "recovery.descriptor.filename");
        RequireEqual(record.Descriptor.Filename, record.ModFolder + ".mod",
            "recovery.descriptor.filename");
        ValidateSha256(record.Descriptor.Sha256, "recovery.descriptor.sha256");
        ValidateGitBlob(record.Descriptor.GitBlob, "recovery.descriptor.git_blob");

        ValidateOutput(record);
        ValidateBuildReceipt(record);
        ValidateManifestEquality(record, binding);
    }

    private static void ValidateOutput(RecoveryRecord record)
    {
        RequireEqual(record.Output.Algorithm, OutputFingerprintAlgorithm, "recovery.output.algorithm");
        ValidateSha256(record.Output.FingerprintSha256, "recovery.output.fingerprint_sha256");

        ValidateUniqueFilenameSet(
            record.Output.Files.Select(row => row.Filename),
            "recovery.output.files");
        string? previous = null;
        RecoveryOutputFile? descriptorRow = null;
        var rootCount = 0;

        for (var i = 0; i < record.Output.Files.Count; i++)
        {
            var row = record.Output.Files[i];
            var path = $"recovery.output.files[{i}]";
            ValidateCanonicalLeaf(row.Filename, $"{path}.filename");
            ValidateOutputFilename(row.Filename, record.Descriptor.Filename, $"{path}.filename");
            if (row.Length <= 0)
                throw Error($"{path}.length must be positive");
            ValidateSha256(row.Sha256, $"{path}.sha256");
            RequireOrdinalOrder(row.Filename, ref previous, "recovery.output.files");

            if (record.BundleAuthority == "tracked")
                ValidateGitBlob(row.GitBlob, $"{path}.git_blob");
            else if (row.GitBlob.Length != 0)
                throw Error($"{path}.git_blob must be empty under receipt authority");

            if (row.Filename == record.Descriptor.Filename)
            {
                if (descriptorRow is not null)
                    throw Error("recovery.output.files contains duplicate descriptor identity");
                descriptorRow = row;
            }
            if (row.Filename == record.RootBundle) rootCount++;
        }

        if (descriptorRow is null)
            throw Error("recovery.output.files is missing the declared descriptor");
        if (rootCount != 1)
            throw Error("recovery.output.files must contain exactly one declared root bundle");
        RequireEqual(descriptorRow.Sha256, record.Descriptor.Sha256,
            "recovery descriptor/output SHA-256");
        if (record.BundleAuthority == "tracked")
            RequireEqual(descriptorRow.GitBlob, record.Descriptor.GitBlob,
                "recovery descriptor/output Git blob");

        var fingerprint = ComputeOutputFingerprint(record.Output.Files);
        RequireEqual(record.Output.FingerprintSha256, fingerprint,
            "recovery.output.fingerprint_sha256");
    }

    private static void ValidateBuildReceipt(RecoveryRecord record)
    {
        var receipt = record.BuildReceipt;
        RequireEqual(receipt.Path, record.ModFolder + "/.build-receipt.json",
            "recovery.build_receipt.path");
        ValidateGitBlob(receipt.GitBlob, "recovery.build_receipt.git_blob");
        ValidateSha256(receipt.Sha256, "recovery.build_receipt.sha256");
        RequireEqual(receipt.SourceAlgorithm, BuildSourceFingerprintAlgorithm,
            "recovery.build_receipt.source_algorithm");
        ValidateSha256(receipt.SourceFingerprintSha256,
            "recovery.build_receipt.source_fingerprint_sha256");
        RequireEqual(receipt.RootBundle, record.RootBundle,
            "recovery.build_receipt.root_bundle");
        RequireEqual(receipt.DescriptorName, record.Descriptor.Filename,
            "recovery.build_receipt.descriptor_name");
        RequireEqual(receipt.DescriptorSha256, record.Descriptor.Sha256,
            "recovery.build_receipt.descriptor_sha256");
        RequireEqual(receipt.OutputAlgorithm, record.Output.Algorithm,
            "recovery.build_receipt.output_algorithm");
        RequireEqual(receipt.OutputFingerprintSha256, record.Output.FingerprintSha256,
            "recovery.build_receipt.output_fingerprint_sha256");
        RequireEqual(receipt.BuilderName, record.Builder.Name,
            "recovery.build_receipt.builder_name");
        RequireEqual(receipt.BuilderVersion, record.Builder.Version,
            "recovery.build_receipt.builder_version");

        var policy = receipt.NormalizationPolicy;
        RequireEqual(policy.Algorithm, NormalizationFingerprintAlgorithm,
            "recovery.build_receipt.normalization_policy.algorithm");
        ValidateSha256(policy.FingerprintSha256,
            "recovery.build_receipt.normalization_policy.fingerprint_sha256");

        ValidateUniqueFilenameSet(
            policy.ExcludedOutputs.Select(row => row.Filename),
            "recovery.build_receipt.normalization_policy.excluded_outputs");
        string? previous = null;
        var outputNames = new HashSet<string>(record.Output.Files.Select(row => row.Filename),
            StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < policy.ExcludedOutputs.Count; i++)
        {
            var row = policy.ExcludedOutputs[i];
            var path = $"recovery.build_receipt.normalization_policy.excluded_outputs[{i}]";
            ValidateRootBundle(row.Filename, $"{path}.filename");
            ValidateSha256(row.Sha256, $"{path}.sha256");
            RequireOrdinalOrder(row.Filename, ref previous,
                "recovery.build_receipt.normalization_policy.excluded_outputs");
            if (row.Filename.Equals(record.RootBundle, StringComparison.OrdinalIgnoreCase))
                throw Error($"{path}.filename cannot name the declared root bundle");
            if (outputNames.Contains(row.Filename))
                throw Error($"{path}.filename cannot also be a normalized output");
        }

        var fingerprint = ComputeNormalizationFingerprint(policy.ExcludedOutputs);
        RequireEqual(policy.FingerprintSha256, fingerprint,
            "recovery.build_receipt.normalization_policy.fingerprint_sha256");
    }

    private static void ValidateManifestEquality(
        RecoveryRecord record,
        RecoveryManifestBinding binding)
    {
        RequireEqual(record.ModId, binding.ModId, "recovery.mod_id/manifest.mod_id");
        RequireEqual(record.WorkshopId, binding.WorkshopId,
            "recovery.workshop_id/manifest.workshop_id");
        RequireEqual(record.Version, binding.Version, "recovery.version/manifest.version");
        RequireEqual(record.Asset.Filename, binding.AssetFilename,
            "recovery.asset.filename/manifest.asset_filename");
        RequireEqual(record.Asset.Sha256, binding.AssetSha256,
            "recovery.asset.sha256/manifest.sha256");
        RequireEqual(record.Source.Commit, binding.SourceCommit,
            "recovery.source.commit/manifest.source_commit");
        RequireEqual(record.Source.State, binding.SourceState,
            "recovery.source.state/manifest.source_state");
        RequireEqual(record.Builder.Name, binding.BuilderName,
            "recovery.builder.name/manifest.builder.name");
        RequireEqual(record.Builder.Version, binding.BuilderVersion,
            "recovery.builder.version/manifest.builder.version");
        RequireEqual(record.BundleAuthority, binding.BundleAuthority,
            "recovery.bundle_authority/manifest.bundle_authority");
        RequireEqual(record.RootBundle, binding.RootBundle,
            "recovery.root_bundle/manifest.root_bundle");
        RequireEqual(record.Descriptor.Filename, binding.DescriptorName,
            "recovery.descriptor.filename/manifest.descriptor_name");

        if (record.Output.Files.Count != binding.BundleFiles.Count)
            throw Error("recovery.output.files count differs from manifest.bundle_files");
        for (var i = 0; i < record.Output.Files.Count; i++)
        {
            RequireEqual(record.Output.Files[i].Filename, binding.BundleFiles[i].Filename,
                $"recovery.output.files[{i}].filename/manifest.bundle_files[{i}].filename");
            RequireEqual(record.Output.Files[i].Sha256, binding.BundleFiles[i].Sha256,
                $"recovery.output.files[{i}].sha256/manifest.bundle_files[{i}].sha256");
        }
    }

    private static string ComputeOutputFingerprint(IReadOnlyList<RecoveryOutputFile> files)
    {
        var builder = new StringBuilder(OutputFingerprintAlgorithm.Length + files.Count * 180);
        builder.Append(OutputFingerprintAlgorithm).Append('\n');
        foreach (var row in files)
        {
            builder.Append(row.Filename).Append('\0')
                .Append(row.Length.ToString(CultureInfo.InvariantCulture)).Append('\0')
                .Append(row.Sha256).Append('\n');
        }
        return Sha256Utf8(builder.ToString());
    }

    private static string ComputeNormalizationFingerprint(
        IReadOnlyList<RecoveryExcludedOutput> excludedOutputs)
    {
        var builder = new StringBuilder(excludedOutputs.Count * 160);
        foreach (var row in excludedOutputs)
            builder.Append(row.Filename).Append('\0').Append(row.Sha256).Append('\n');
        return Sha256Utf8(builder.ToString());
    }

    private static string ComputeSemanticEquivalenceDigest(RecoveryRecord record)
    {
        using var writer = new TypedSemanticDigestWriter(SemanticEquivalenceAlgorithm);
        writer.Integer("schema", record.Schema);
        writer.String("release.repository", record.Release.Repository);
        writer.String("release.tag", record.Release.Tag);
        writer.String("mod_folder", record.ModFolder);
        writer.String("mod_id", record.ModId);
        writer.String("workshop_id", record.WorkshopId);
        writer.String("version", record.Version);
        writer.String("asset.filename", record.Asset.Filename);
        writer.Integer("asset.length", record.Asset.Length);
        writer.String("asset.sha256", record.Asset.Sha256);
        writer.String("source.commit", record.Source.Commit);
        writer.String("source.state", record.Source.State);
        writer.String("source.item_cfg_sha256", record.Source.ItemCfgSha256);
        writer.String("source.item_cfg_git_blob", record.Source.ItemCfgGitBlob);
        writer.String("builder.name", record.Builder.Name);
        writer.String("builder.version", record.Builder.Version);
        writer.String("bundle_authority", record.BundleAuthority);
        writer.String("authority_proof.byte_source", record.AuthorityProof.ByteSource);
        writer.String("authority_proof.inventory_git_blob", record.AuthorityProof.InventoryGitBlob);
        writer.String("authority_proof.ignore_git_blob", record.AuthorityProof.IgnoreGitBlob);
        writer.String("root_bundle", record.RootBundle);
        writer.String("descriptor.filename", record.Descriptor.Filename);
        writer.String("descriptor.sha256", record.Descriptor.Sha256);
        writer.String("descriptor.git_blob", record.Descriptor.GitBlob);
        writer.String("output.algorithm", record.Output.Algorithm);
        writer.String("output.fingerprint_sha256", record.Output.FingerprintSha256);
        writer.Array("output.files", record.Output.Files.Count);
        for (var i = 0; i < record.Output.Files.Count; i++)
        {
            var row = record.Output.Files[i];
            writer.String($"output.files[{i}].filename", row.Filename);
            writer.Integer($"output.files[{i}].length", row.Length);
            writer.String($"output.files[{i}].sha256", row.Sha256);
            writer.String($"output.files[{i}].git_blob", row.GitBlob);
        }

        var receipt = record.BuildReceipt;
        writer.String("build_receipt.path", receipt.Path);
        writer.Integer("build_receipt.schema", receipt.Schema);
        writer.String("build_receipt.git_blob", receipt.GitBlob);
        writer.String("build_receipt.sha256", receipt.Sha256);
        writer.String("build_receipt.source_algorithm", receipt.SourceAlgorithm);
        writer.String("build_receipt.source_fingerprint_sha256", receipt.SourceFingerprintSha256);
        writer.String("build_receipt.root_bundle", receipt.RootBundle);
        writer.String("build_receipt.descriptor_name", receipt.DescriptorName);
        writer.String("build_receipt.descriptor_sha256", receipt.DescriptorSha256);
        writer.String("build_receipt.output_algorithm", receipt.OutputAlgorithm);
        writer.String("build_receipt.output_fingerprint_sha256", receipt.OutputFingerprintSha256);
        writer.String("build_receipt.builder_name", receipt.BuilderName);
        writer.String("build_receipt.builder_version", receipt.BuilderVersion);
        writer.String("build_receipt.normalization_policy.algorithm", receipt.NormalizationPolicy.Algorithm);
        writer.String("build_receipt.normalization_policy.fingerprint_sha256",
            receipt.NormalizationPolicy.FingerprintSha256);
        writer.Array("build_receipt.normalization_policy.excluded_outputs",
            receipt.NormalizationPolicy.ExcludedOutputs.Count);
        for (var i = 0; i < receipt.NormalizationPolicy.ExcludedOutputs.Count; i++)
        {
            var row = receipt.NormalizationPolicy.ExcludedOutputs[i];
            writer.String($"build_receipt.normalization_policy.excluded_outputs[{i}].filename",
                row.Filename);
            writer.String($"build_receipt.normalization_policy.excluded_outputs[{i}].sha256",
                row.Sha256);
        }

        return writer.Finish();
    }

    private static string Sha256Utf8(string value)
    {
        var bytes = StrictUtf8.GetBytes(value);
        return Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    }

    private static JsonElement RequireArray(JsonElement element, string path)
    {
        if (element.ValueKind != JsonValueKind.Array)
            throw Error($"{path} must be a JSON array");
        return element;
    }

    private static string ReadString(
        JsonElement element,
        string path,
        int maxUtf8Bytes,
        bool allowEmpty = false)
    {
        if (element.ValueKind != JsonValueKind.String)
            throw Error($"{path} must be a JSON string");
        var value = element.GetString()
            ?? throw Error($"{path} must not be null");
        if (!allowEmpty && value.Length == 0)
            throw Error($"{path} must not be empty");
        if (GetUtf8ByteCount(value, path) > maxUtf8Bytes)
            throw Error($"{path} exceeds its {maxUtf8Bytes}-byte bound");
        return value;
    }

    private static long ReadPositiveInt64(JsonElement element, string path)
    {
        if (element.ValueKind != JsonValueKind.Number)
            throw Error($"{path} must be a JSON integer number");
        var raw = element.GetRawText();
        if (!PositiveDecimalPattern.IsMatch(raw) ||
            !long.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out var value) ||
            value <= 0)
        {
            throw Error($"{path} must be a canonical positive Int64");
        }
        return value;
    }

    private static void ReadExactInteger(JsonElement element, string path, int expected)
    {
        if (element.ValueKind != JsonValueKind.Number ||
            !string.Equals(element.GetRawText(), expected.ToString(CultureInfo.InvariantCulture),
                StringComparison.Ordinal))
        {
            throw Error($"{path} must be the canonical integer {expected}");
        }
    }

    private static void ValidateModFolder(string value, string path)
    {
        if (string.IsNullOrEmpty(value) || !ModFolderPattern.IsMatch(value) ||
            GetUtf8ByteCount(value, path) > MaxIdentifierBytes)
            throw Error($"{path} is not canonical");
    }

    private static void ValidateModId(string value, string path)
    {
        if (string.IsNullOrEmpty(value) || !ModIdPattern.IsMatch(value) ||
            GetUtf8ByteCount(value, path) > MaxIdentifierBytes)
            throw Error($"{path} is not canonical");
    }

    private static void ValidateWorkshopId(string value, string path)
    {
        if (string.IsNullOrEmpty(value) || !PositiveDecimalPattern.IsMatch(value) ||
            !ulong.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) ||
            parsed == 0)
        {
            throw Error($"{path} must be a canonical positive UInt64 string");
        }
    }

    private static void ValidateCanonicalText(string value, string path, int maxUtf8Bytes)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            !string.Equals(value, value.Trim(), StringComparison.Ordinal) ||
            value.Any(char.IsControl) ||
            GetUtf8ByteCount(value, path) > maxUtf8Bytes)
        {
            throw Error($"{path} is empty, non-canonical, or exceeds its {maxUtf8Bytes}-byte bound");
        }
    }

    private static void ValidateAuthority(string value, string path)
    {
        if (value is not ("tracked" or "receipt"))
            throw Error($"{path} must be exactly 'tracked' or 'receipt'");
    }

    private static void ValidateAssetFilename(string value, string modId, string path)
    {
        ValidateCanonicalLeaf(value, path);
        if (!ZipAssetPattern.IsMatch(value) || !string.Equals(value, modId + ".zip", StringComparison.Ordinal))
            throw Error($"{path} must be the exact case-sensitive <mod_id>.zip leaf");
    }

    private static void ValidateOutputFilename(string value, string descriptorName, string path)
    {
        if (!string.Equals(value, descriptorName, StringComparison.Ordinal) &&
            !RootBundlePattern.IsMatch(value))
        {
            throw Error($"{path} is neither the exact descriptor nor a canonical bundle leaf");
        }
    }

    private static void ValidateRootBundle(string value, string path)
    {
        ValidateCanonicalLeaf(value, path);
        if (!RootBundlePattern.IsMatch(value))
            throw Error($"{path} must be a lowercase 16-hex .mod_bundle leaf");
    }

    private static void ValidateCanonicalLeaf(string value, string path)
    {
        if (string.IsNullOrEmpty(value) ||
            GetUtf8ByteCount(value, path) > 256 ||
            value is "." or ".." ||
            value.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
            value.Contains('/') || value.Contains('\\') || value.Contains(':') ||
            !string.Equals(Path.GetFileName(value), value, StringComparison.Ordinal) ||
            !string.Equals(value.TrimEnd(' ', '.'), value, StringComparison.Ordinal) ||
            value.Any(char.IsControl))
        {
            throw Error($"{path} must be one canonical Windows leaf filename");
        }

        var stem = Path.GetFileNameWithoutExtension(value).ToUpperInvariant();
        if (stem is "CON" or "PRN" or "AUX" or "NUL" ||
            Regex.IsMatch(stem, "\\A(COM|LPT)[1-9]\\z", RegexOptions.CultureInvariant))
        {
            throw Error($"{path} uses a reserved Windows device name");
        }
    }

    private static void ValidateSha256(string value, string path)
    {
        if (string.IsNullOrEmpty(value) || !LowerSha256Pattern.IsMatch(value))
            throw Error($"{path} must be exactly 64 lowercase hexadecimal characters");
    }

    private static void ValidateGitBlob(string value, string path)
    {
        if (string.IsNullOrEmpty(value) || !LowerGitBlobPattern.IsMatch(value))
            throw Error($"{path} must be a lowercase 40- or 64-character Git blob id");
    }

    private static void ValidateLowerHex(string value, int length, string path)
    {
        if (value is null || value.Length != length ||
            value.Any(c => c is not (>= '0' and <= '9') and not (>= 'a' and <= 'f')))
            throw Error($"{path} must be exactly {length} lowercase hexadecimal characters");
    }

    private static void ValidateUniqueFilenameSet(
        IEnumerable<string?> names,
        string path)
    {
        var ordinalNames = new HashSet<string>(StringComparer.Ordinal);
        var foldedNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var nullableName in names)
        {
            var name = nullableName ?? throw Error($"{path} contains a null filename");
            if (!ordinalNames.Add(name))
                throw Error($"{path} contains duplicate filename '{name}'");
            if (foldedNames.TryGetValue(name, out var prior))
                throw Error($"{path} contains case-colliding filenames '{prior}' and '{name}'");
            foldedNames.Add(name, name);
        }
    }

    private static void RequireOrdinalOrder(string name, ref string? previous, string path)
    {
        if (previous is not null && StringComparer.Ordinal.Compare(previous, name) >= 0)
            throw Error($"{path} is not in strict canonical ordinal order");
        previous = name;
    }

    private static void RequireEqual(string actual, string expected, string path)
    {
        if (!string.Equals(actual, expected, StringComparison.Ordinal))
            throw Error($"{path} differs: expected '{expected}', got '{actual}'");
    }

    private static int GetUtf8ByteCount(string value, string path)
    {
        try
        {
            return StrictUtf8.GetByteCount(value);
        }
        catch (EncoderFallbackException ex)
        {
            throw Error($"{path} contains invalid Unicode", ex);
        }
    }

    private static RecoveryRecordValidationException Error(string message, Exception? inner = null) =>
        new(message, inner);

    private sealed class StrictObject
    {
        private readonly Dictionary<string, JsonElement> _properties;

        private StrictObject(Dictionary<string, JsonElement> properties) => _properties = properties;

        public JsonElement this[string name] => _properties[name];

        public static StrictObject Read(JsonElement element, string path, params string[] expectedNames)
        {
            if (element.ValueKind != JsonValueKind.Object)
                throw Error($"{path} must be a JSON object");

            var expected = new HashSet<string>(expectedNames, StringComparer.Ordinal);
            var properties = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (!properties.TryAdd(property.Name, property.Value))
                    throw Error($"{path} contains duplicate property '{property.Name}'");
                if (!expected.Contains(property.Name))
                    throw Error($"{path} contains unsupported property '{property.Name}'");
            }

            foreach (var expectedName in expectedNames)
            {
                if (!properties.ContainsKey(expectedName))
                    throw Error($"{path} is missing required property '{expectedName}'");
            }
            return new StrictObject(properties);
        }
    }

    private sealed class TypedSemanticDigestWriter : IDisposable
    {
        private readonly IncrementalHash _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        private bool _finished;

        public TypedSemanticDigestWriter(string domain)
        {
            AppendByte(0x7f);
            AppendUtf8(domain);
        }

        public void String(string path, string value)
        {
            Header(0x01, path);
            AppendUtf8(value);
        }

        public void Integer(string path, long value)
        {
            Header(0x02, path);
            Span<byte> bytes = stackalloc byte[8];
            BinaryPrimitives.WriteInt64BigEndian(bytes, value);
            _hash.AppendData(bytes);
        }

        public void Array(string path, int count)
        {
            Header(0x03, path);
            Span<byte> bytes = stackalloc byte[4];
            BinaryPrimitives.WriteInt32BigEndian(bytes, count);
            _hash.AppendData(bytes);
        }

        public string Finish()
        {
            if (_finished) throw new InvalidOperationException("Semantic digest was already finalized.");
            _finished = true;
            return Convert.ToHexString(_hash.GetHashAndReset()).ToLowerInvariant();
        }

        public void Dispose() => _hash.Dispose();

        private void Header(byte kind, string path)
        {
            AppendByte(kind);
            AppendUtf8(path);
        }

        private void AppendByte(byte value) => _hash.AppendData(new[] { value });

        private void AppendUtf8(string value)
        {
            var bytes = StrictUtf8.GetBytes(value);
            Span<byte> length = stackalloc byte[4];
            BinaryPrimitives.WriteInt32BigEndian(length, bytes.Length);
            _hash.AppendData(length);
            _hash.AppendData(bytes);
        }
    }
}

public sealed class RecoveryRecordValidationException : Exception
{
    public RecoveryRecordValidationException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }
}
