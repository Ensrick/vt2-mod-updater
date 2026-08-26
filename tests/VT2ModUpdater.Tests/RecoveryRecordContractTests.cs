using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using VT2ModUpdater.Models;
using VT2ModUpdater.Services;

namespace VT2ModUpdater.Tests;

public class RecoveryRecordContractTests
{
    private const string GoldenAssetSha =
        "7d1f642208d5851b8cfa748e4207093c24de70a2a6377b2473b1b1996d86b4e0";
    private const string DescriptorSha =
        "6db3ae2ce8ed0d57f22fb35a5beaa8cb0ec35ec9d560b829e582dd4d63ea78f3";

    public static IEnumerable<object[]> TrailingLineTerminatorCases()
    {
        var exactStrings = new (string Path, string Value)[]
        {
            ("release.repository", "Ensrick/vermintide-2-tweaker"),
            ("release.tag", "mods-fixture-2026-08-26"),
            ("mod_folder", "modx"),
            ("mod_id", "mx"),
            ("workshop_id", "1234567890"),
            ("version", "1.2.3-dev"),
            ("asset.filename", "mx.zip"),
            ("asset.sha256", GoldenAssetSha),
            ("source.commit", "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"),
            ("source.state", "clean"),
            ("source.item_cfg_sha256",
                "9e11fb7137a91b37e77306c0e074b8078c4f4639edeb17fc0c5f5ed6257f83b6"),
            ("source.item_cfg_git_blob", "3333333333333333333333333333333333333333"),
            ("builder.name", "VMBLauncher"),
            ("builder.version", "9.8.7+fixture"),
            ("bundle_authority", "tracked"),
            ("authority_proof.byte_source", "git_commit_blobs"),
            ("authority_proof.inventory_git_blob", "1111111111111111111111111111111111111111"),
            ("authority_proof.ignore_git_blob", "2222222222222222222222222222222222222222"),
            ("root_bundle", "0123456789abcdef.mod_bundle"),
            ("descriptor.filename", "modx.mod"),
            ("descriptor.sha256", DescriptorSha),
            ("descriptor.git_blob", "4444444444444444444444444444444444444444"),
            ("output.algorithm", RecoveryRecordContract.OutputFingerprintAlgorithm),
            ("output.fingerprint_sha256",
                "30bea7ad8acb4dd6b502e824565facc4a39abccfc514d594d805f3974f0f43b2"),
            ("output.files[0].filename", "0123456789abcdef.mod_bundle"),
            ("output.files[0].sha256",
                "57f4bc8fc7f9a9271afe6d3d0aed6afc675f06b6f6fb738b838d4f53da60f5c6"),
            ("output.files[0].git_blob", "5555555555555555555555555555555555555555"),
            ("build_receipt.path", "modx/.build-receipt.json"),
            ("build_receipt.git_blob", "9999999999999999999999999999999999999999"),
            ("build_receipt.sha256",
                "b1ead543f5253bfc920b140c8b61a865e392038543db7829e3b2dd0582418046"),
            ("build_receipt.source_algorithm", RecoveryRecordContract.BuildSourceFingerprintAlgorithm),
            ("build_receipt.source_fingerprint_sha256", new string('a', 64)),
            ("build_receipt.root_bundle", "0123456789abcdef.mod_bundle"),
            ("build_receipt.descriptor_name", "modx.mod"),
            ("build_receipt.descriptor_sha256", DescriptorSha),
            ("build_receipt.output_algorithm", RecoveryRecordContract.OutputFingerprintAlgorithm),
            ("build_receipt.output_fingerprint_sha256",
                "30bea7ad8acb4dd6b502e824565facc4a39abccfc514d594d805f3974f0f43b2"),
            ("build_receipt.builder_name", "VMBLauncher"),
            ("build_receipt.builder_version", "9.8.7+fixture"),
            ("build_receipt.normalization_policy.algorithm",
                RecoveryRecordContract.NormalizationFingerprintAlgorithm),
            ("build_receipt.normalization_policy.fingerprint_sha256",
                "2ca5530e1750551ccedd160ab6a267b56cf713ec6ddcb21ea8820481cecebd8c"),
            ("build_receipt.normalization_policy.excluded_outputs[0].filename",
                "0a0b0c0d0e0f1011.mod_bundle"),
            ("build_receipt.normalization_policy.excluded_outputs[0].sha256",
                new string('7', 64))
        };

        foreach (var (path, value) in exactStrings)
        {
            foreach (var terminator in new[] { "\r", "\n", "\r\n" })
                yield return new object[] { path, value, terminator };
        }
    }

    [Fact]
    public void ProducerGolden_ValidatesCompleteTrackedContract()
    {
        var result = ParseGolden();

        Assert.Equal(1, result.Record.Schema);
        Assert.Equal("mods-fixture-2026-08-26", result.Record.Release.Tag);
        Assert.Equal("tracked", result.Record.BundleAuthority);
        Assert.Equal(3, result.Record.Output.Files.Count);
        Assert.Equal(
            "30bea7ad8acb4dd6b502e824565facc4a39abccfc514d594d805f3974f0f43b2",
            result.Record.Output.FingerprintSha256);
        Assert.Equal(
            "2ca5530e1750551ccedd160ab6a267b56cf713ec6ddcb21ea8820481cecebd8c",
            result.Record.BuildReceipt.NormalizationPolicy.FingerprintSha256);
        Assert.Equal(RecoveryRecordContract.SemanticEquivalenceAlgorithm,
            result.SemanticEquivalenceAlgorithm);
        Assert.Equal(
            "5f69003f04ba0a798751550b3104ec98e6562fd9eb016977b45805eaae9ae54e",
            result.SemanticEquivalenceSha256);
    }

    [Fact]
    public void SemanticDigest_IsStableAcrossJsonObjectPropertyOrder()
    {
        var original = ParseGolden();
        var root = ParseGoldenNode();
        var reordered = new JsonObject();
        foreach (var property in root.Reverse())
            reordered[property.Key] = property.Value?.DeepClone();

        var parsed = RecoveryRecordContract.ParseAndValidate(
            reordered.ToJsonString(), GoldenBinding());

        Assert.Equal(original.Record.Output.Files.ToArray(), parsed.Record.Output.Files.ToArray());
        Assert.Equal(
            original.Record.BuildReceipt.NormalizationPolicy.ExcludedOutputs.ToArray(),
            parsed.Record.BuildReceipt.NormalizationPolicy.ExcludedOutputs.ToArray());
        Assert.Equal(original.SemanticEquivalenceSha256, parsed.SemanticEquivalenceSha256);
    }

    [Fact]
    public void ReceiptAuthority_UsesEmptyOutputGitBlobsAndDistinctByteSource()
    {
        var json = FixtureJson("valid-receipt.json");
        var binding = GoldenBinding() with
        {
            SourceCommit = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",
            BundleAuthority = "receipt"
        };

        var result = RecoveryRecordContract.ParseAndValidate(json, binding);

        Assert.Equal("receipt", result.Record.BundleAuthority);
        Assert.All(result.Record.Output.Files, row => Assert.Equal("", row.GitBlob));
        Assert.NotEqual(ParseGolden().SemanticEquivalenceSha256,
            result.SemanticEquivalenceSha256);
    }

    [Fact]
    public void EmptyNormalizationPolicy_UsesProducerEmptySha256()
    {
        var json = Mutate(root =>
        {
            var policy = root["build_receipt"]!["normalization_policy"]!;
            policy["excluded_outputs"] = new JsonArray();
            policy["fingerprint_sha256"] =
                "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855";
        });

        var result = RecoveryRecordContract.ParseAndValidate(json, GoldenBinding());

        Assert.Empty(result.Record.BuildReceipt.NormalizationPolicy.ExcludedOutputs);
    }

    [Theory]
    [InlineData("schema", "1.0")]
    [InlineData("schema", "1e0")]
    [InlineData("build_receipt.schema", "3.0")]
    [InlineData("asset.length", "5.46e2")]
    [InlineData("output.files[0].length", "8.0")]
    public void NonCanonicalJsonNumbers_AreRejected(string path, string rawNumber)
    {
        var json = Mutate(root => SetPath(root, path, JsonNode.Parse(rawNumber)));

        AssertRejected(json);
    }

    [Theory]
    [InlineData("asset.length")]
    [InlineData("output.files[0].length")]
    public void Int64Overflow_IsRejected(string path)
    {
        var json = Mutate(root =>
            SetPath(root, path, JsonNode.Parse("9223372036854775808")));

        AssertRejected(json);
    }

    [Fact]
    public void WorkshopIdUInt64Overflow_IsRejectedBeforeEquivalence()
    {
        var json = Mutate(root => root["workshop_id"] = "18446744073709551616");

        AssertRejected(json);
    }

    [Theory]
    [MemberData(nameof(TrailingLineTerminatorCases))]
    public void ExactStringAxes_RejectTrailingLineTerminatorVariants(
        string path,
        string value,
        string terminator)
    {
        var json = Mutate(root => SetPath(root, path, value + terminator));

        AssertRejected(json);
    }

    [Fact]
    public void CoherentDescriptorAndOutputGitBlobWithTrailingLf_IsRejected()
    {
        var forgedBlob = "4444444444444444444444444444444444444444\n";
        var json = Mutate(root =>
        {
            root["descriptor"]!["git_blob"] = forgedBlob;
            root["output"]!["files"]![2]!["git_blob"] = forgedBlob;
        });

        AssertRejected(json);
    }

    [Fact]
    public void DuplicateRootProperty_IsRejectedWithoutLastValueWins()
    {
        var json = GoldenJson().Replace(
            "\"schema\": 1,",
            "\"schema\": 1,\n  \"schema\": 1,",
            StringComparison.Ordinal);

        var exception = AssertRejected(json);
        Assert.Contains("duplicate property 'schema'", exception.Message);
    }

    [Fact]
    public void DuplicateNestedProperty_IsRejectedWithoutLastValueWins()
    {
        var json = GoldenJson().Replace(
            "\"repository\": \"Ensrick/vermintide-2-tweaker\",",
            "\"repository\": \"Ensrick/vermintide-2-tweaker\",\n    " +
            "\"repository\": \"Ensrick/vermintide-2-tweaker\",",
            StringComparison.Ordinal);

        var exception = AssertRejected(json);
        Assert.Contains("duplicate property 'repository'", exception.Message);
    }

    [Theory]
    [InlineData("root")]
    [InlineData("asset")]
    [InlineData("output-row")]
    [InlineData("normalization-row")]
    public void UnknownProperties_AreRejectedAtEverySchemaDepth(string location)
    {
        var json = Mutate(root =>
        {
            var target = location switch
            {
                "root" => root,
                "asset" => root["asset"]!.AsObject(),
                "output-row" => root["output"]!["files"]![0]!.AsObject(),
                "normalization-row" => root["build_receipt"]!["normalization_policy"]!
                    ["excluded_outputs"]![0]!.AsObject(),
                _ => throw new InvalidOperationException()
            };
            target["forged"] = true;
        });

        var exception = AssertRejected(json);
        Assert.Contains("unsupported property 'forged'", exception.Message);
    }

    [Theory]
    [InlineData("asset", "null")]
    [InlineData("output.files", "null")]
    [InlineData("build_receipt.normalization_policy.excluded_outputs", "null")]
    [InlineData("descriptor.sha256", "123")]
    public void WrongJsonTypesAndNullCollections_AreRejected(string path, string rawJson)
    {
        var json = Mutate(root => SetPath(root, path, JsonNode.Parse(rawJson)));

        AssertRejected(json);
    }

    [Theory]
    [InlineData("asset.sha256", "0000000000000000000000000000000000000000000000000000000000000000")]
    [InlineData("asset.filename", "other.zip")]
    [InlineData("source.commit", "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb")]
    [InlineData("builder.version", "other-builder")]
    [InlineData("root_bundle", "fedcba9876543210.mod_bundle")]
    [InlineData("descriptor.filename", "other.mod")]
    [InlineData("descriptor.sha256", "1111111111111111111111111111111111111111111111111111111111111111")]
    [InlineData("build_receipt.path", "other/.build-receipt.json")]
    [InlineData("build_receipt.root_bundle", "fedcba9876543210.mod_bundle")]
    [InlineData("build_receipt.descriptor_name", "other.mod")]
    [InlineData("build_receipt.output_algorithm", "other-output")]
    [InlineData("build_receipt.output_fingerprint_sha256", "2222222222222222222222222222222222222222222222222222222222222222")]
    [InlineData("build_receipt.builder_version", "other-builder")]
    [InlineData("build_receipt.normalization_policy.algorithm", "other-policy")]
    public void CrossFieldOrParentProofDrift_IsRejected(string path, string replacement)
    {
        var json = Mutate(root => SetPath(root, path, replacement));

        AssertRejected(json);
    }

    [Fact]
    public void OutputFingerprint_IsRecomputedFromCanonicalTypedRows()
    {
        var json = Mutate(root => root["output"]!["files"]![0]!["length"] = 9);

        var exception = AssertRejected(json);
        Assert.Contains("output.fingerprint_sha256", exception.Message);
    }

    [Fact]
    public void NormalizationFingerprint_IsRecomputedFromCanonicalRows()
    {
        var json = Mutate(root =>
            root["build_receipt"]!["normalization_policy"]!["excluded_outputs"]![0]!
                ["sha256"] = new string('9', 64));

        var exception = AssertRejected(json);
        Assert.Contains("normalization_policy.fingerprint_sha256", exception.Message);
    }

    [Fact]
    public void OutputRowsMustRemainInCanonicalOrdinalOrder()
    {
        var json = Mutate(root =>
        {
            var rows = root["output"]!["files"]!.AsArray();
            var first = rows[0]!.DeepClone();
            var second = rows[1]!.DeepClone();
            rows[0] = second;
            rows[1] = first;
        });

        var exception = AssertRejected(json);
        Assert.Contains("canonical ordinal order", exception.Message);
    }

    [Fact]
    public void OutputCaseCollision_IsRejected()
    {
        var json = Mutate(root =>
        {
            var rows = root["output"]!["files"]!.AsArray();
            var collision = rows[2]!.DeepClone();
            collision!["filename"] = "MODX.mod";
            rows.Insert(2, collision);
        });

        var exception = AssertRejected(json);
        Assert.Contains("case-colliding", exception.Message);
    }

    [Fact]
    public void ManifestOutputCaseDetachment_IsRejectedEvenWhenHashMatches()
    {
        var binding = GoldenBinding() with
        {
            BundleFiles = Array.AsReadOnly(new[]
            {
                new RecoveryManifestBundleFile("0123456789abcdef.mod_bundle",
                    "57f4bc8fc7f9a9271afe6d3d0aed6afc675f06b6f6fb738b838d4f53da60f5c6"),
                new RecoveryManifestBundleFile("FEDCBA9876543210.mod_bundle",
                    "92b80db12ef207bda13fb28ade13297e316259f357d339c2fc84393854402cb5"),
                new RecoveryManifestBundleFile("modx.mod", DescriptorSha)
            })
        };

        AssertRejected(GoldenJson(), binding);
    }

    [Fact]
    public void ExactParentOutputMapIsRequired()
    {
        var binding = GoldenBinding() with
        {
            BundleFiles = Array.AsReadOnly(GoldenBinding().BundleFiles.Skip(1).ToArray())
        };

        AssertRejected(GoldenJson(), binding);
    }

    [Fact]
    public void AuthoritySpecificOutputGitBlobRulesFailClosed()
    {
        var trackedWithoutBlob = Mutate(root =>
            root["output"]!["files"]![0]!["git_blob"] = "");
        AssertRejected(trackedWithoutBlob);

        var receiptWithTrackedBlob = Mutate(root =>
        {
            root["bundle_authority"] = "receipt";
            root["authority_proof"]!["byte_source"] = "materialized_restrictive_handles";
        });
        AssertRejected(receiptWithTrackedBlob,
            GoldenBinding() with { BundleAuthority = "receipt" });
    }

    [Fact]
    public void ExclusionCannotNameRootOrSurvivingOutput()
    {
        var json = Mutate(root =>
        {
            var policy = root["build_receipt"]!["normalization_policy"]!;
            policy["excluded_outputs"]![0]!["filename"] = "0123456789abcdef.mod_bundle";
        });

        AssertRejected(json);
    }

    [Fact]
    public void OversizedJson_IsRejectedBeforeParsing()
    {
        var json = new string(' ', RecoveryRecordContract.MaxJsonUtf8Bytes + 1);

        AssertRejected(json);
    }

    [Fact]
    public void MissingRequiredProperty_IsRejected()
    {
        var json = Mutate(root => root.Remove("build_receipt"));

        var exception = AssertRejected(json);
        Assert.Contains("missing required property 'build_receipt'", exception.Message);
    }

    [Fact]
    public void OutputCountBound_IsEnforcedBeforeRowParsing()
    {
        var json = Mutate(root =>
        {
            var template = root["output"]!["files"]![0]!.DeepClone();
            var rows = new JsonArray();
            for (var i = 0; i <= RecoveryRecordContract.MaxOutputFiles; i++)
                rows.Add(template.DeepClone());
            root["output"]!["files"] = rows;
        });

        var exception = AssertRejected(json);
        Assert.Contains("1..4096", exception.Message);
    }

    [Fact]
    public void NormalizationExclusionCountBound_IsEnforcedBeforeRowParsing()
    {
        var json = Mutate(root =>
        {
            var template = root["build_receipt"]!["normalization_policy"]!
                ["excluded_outputs"]![0]!.DeepClone();
            var rows = new JsonArray();
            for (var i = 0; i <= RecoveryRecordContract.MaxExcludedOutputs; i++)
                rows.Add(template.DeepClone());
            root["build_receipt"]!["normalization_policy"]!["excluded_outputs"] = rows;
        });

        var exception = AssertRejected(json);
        Assert.Contains("4096-entry", exception.Message);
    }

    [Fact]
    public void SemanticDigestIncludesTypedAssetLength()
    {
        var original = ParseGolden();
        var changedJson = Mutate(root => root["asset"]!["length"] = 547);

        var changed = RecoveryRecordContract.ParseAndValidate(changedJson, GoldenBinding());

        Assert.NotEqual(original.SemanticEquivalenceSha256,
            changed.SemanticEquivalenceSha256);
    }

    private static ValidatedRecoveryRecord ParseGolden() =>
        RecoveryRecordContract.ParseAndValidate(GoldenJson(), GoldenBinding());

    private static RecoveryRecordValidationException AssertRejected(
        string json,
        RecoveryManifestBinding? binding = null) =>
        Assert.Throws<RecoveryRecordValidationException>(() =>
            RecoveryRecordContract.ParseAndValidate(json, binding ?? GoldenBinding()));

    private static RecoveryManifestBinding GoldenBinding() => new(
        "mx",
        "1234567890",
        "1.2.3-dev",
        "mx.zip",
        GoldenAssetSha,
        "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
        "clean",
        "VMBLauncher",
        "9.8.7+fixture",
        "tracked",
        "0123456789abcdef.mod_bundle",
        "modx.mod",
        Array.AsReadOnly(new[]
        {
            new RecoveryManifestBundleFile(
                "0123456789abcdef.mod_bundle",
                "57f4bc8fc7f9a9271afe6d3d0aed6afc675f06b6f6fb738b838d4f53da60f5c6"),
            new RecoveryManifestBundleFile(
                "fedcba9876543210.mod_bundle",
                "92b80db12ef207bda13fb28ade13297e316259f357d339c2fc84393854402cb5"),
            new RecoveryManifestBundleFile("modx.mod", DescriptorSha)
        }));

    private static JsonObject ParseGoldenNode() =>
        JsonNode.Parse(GoldenJson())!.AsObject();

    private static string Mutate(Action<JsonObject> mutate)
    {
        var root = ParseGoldenNode();
        mutate(root);
        return root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    private static void SetPath(JsonObject root, string path, string value) =>
        SetPath(root, path, JsonValue.Create(value));

    private static void SetPath(JsonObject root, string path, JsonNode? value)
    {
        var segments = path.Split('.');
        JsonNode current = root;
        for (var i = 0; i < segments.Length - 1; i++)
        {
            var (property, index) = ParseSegment(segments[i]);
            current = current[property]
                ?? throw new InvalidOperationException($"Missing fixture path {segments[i]}");
            if (index is not null)
                current = current[index.Value]
                    ?? throw new InvalidOperationException($"Missing fixture index {segments[i]}");
        }

        var (leafProperty, leafIndex) = ParseSegment(segments[^1]);
        if (leafIndex is null)
        {
            current[leafProperty] = value;
        }
        else
        {
            var array = current[leafProperty]!.AsArray();
            array[leafIndex.Value] = value;
        }
    }

    private static (string Property, int? Index) ParseSegment(string segment)
    {
        var bracket = segment.IndexOf('[', StringComparison.Ordinal);
        if (bracket < 0) return (segment, null);
        var property = segment[..bracket];
        var indexText = segment[(bracket + 1)..^1];
        return (property, int.Parse(indexText));
    }

    private static string GoldenJson() => FixtureJson("valid-tracked.json");

    private static string FixtureJson(string name) => File.ReadAllText(Path.Combine(
        AppContext.BaseDirectory,
        "Fixtures",
        "RecoveryRecords",
        name));
}
