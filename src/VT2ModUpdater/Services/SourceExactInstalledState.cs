using System.Security.Cryptography;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using VT2ModUpdater.Models;

namespace VT2ModUpdater.Services;

/// <summary>
/// Strict installed-state proof for source-exact recovery.  This is deliberately
/// separate from the permissive legacy <c>.vt2updater_sha256.txt</c> sidecar: an
/// absent or malformed source-exact record must never be interpreted as legacy
/// authority and legacy bytes must never be promoted to source-exact authority.
/// </summary>
internal static class SourceExactInstalledState
{
    internal const int SchemaVersion = 1;
    internal const string Authority = "source_exact";
    internal const string Filename = ".vt2updater_source_exact.json";
    internal const int MaximumBytes = 256 * 1024;

    private static readonly Regex LowerSha256 = new(
        "\\A[0-9a-f]{64}\\z",
        RegexOptions.CultureInvariant);
    private static readonly Regex Commit = new(
        "\\A[0-9a-f]{40}\\z",
        RegexOptions.CultureInvariant);

    internal static SourceExactInstalledStateDocument Create(
        SourceExactRecoveryArtifact artifact,
        IReadOnlyList<SourceExactStagedOutput> outputs)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        ArgumentNullException.ThrowIfNull(outputs);
        var proof = artifact.Proof.Record;
        var rows = outputs
            .OrderBy(row => row.Filename, StringComparer.Ordinal)
            .Select(row => new SourceExactInstalledOutput(
                row.Filename,
                row.Length,
                row.Sha256))
            .ToArray();
        var proofRows = proof.Output.Files
            .OrderBy(row => row.Filename, StringComparer.Ordinal)
            .Select(row => new SourceExactInstalledOutput(
                row.Filename,
                row.Length,
                row.Sha256))
            .ToArray();
        if (!rows.SequenceEqual(proofRows))
            throw new InvalidDataException(
                "source-exact staged outputs differ from the recovery proof");
        var document = new SourceExactInstalledStateDocument(
            SchemaVersion,
            Authority,
            proof.ModId,
            proof.WorkshopId,
            proof.Source.Commit,
            artifact.OriginReleaseTag,
            artifact.ContainerReleaseId,
            artifact.AssetId,
            artifact.AssetFilename,
            artifact.AssetLength,
            artifact.AssetSha256,
            proof.Output.FingerprintSha256,
            Array.AsReadOnly(rows));
        Validate(document);
        return document;
    }

    internal static byte[] Serialize(SourceExactInstalledStateDocument document)
    {
        Validate(document);
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions
        {
            Indented = false,
            SkipValidation = false
        }))
        {
            writer.WriteStartObject();
            writer.WriteNumber("schema_version", document.SchemaVersion);
            writer.WriteString("authority", document.Authority);
            writer.WriteString("mod_id", document.ModId);
            writer.WriteString("workshop_id", document.WorkshopId);
            writer.WriteString("source_commit", document.SourceCommit);
            writer.WriteString("origin_release_tag", document.OriginReleaseTag);
            writer.WriteNumber("container_release_id", document.ContainerReleaseId);
            writer.WriteNumber("asset_id", document.AssetId);
            writer.WriteString("asset_filename", document.AssetFilename);
            writer.WriteNumber("asset_length", document.AssetLength);
            writer.WriteString("asset_sha256", document.AssetSha256);
            writer.WriteString("output_fingerprint", document.OutputFingerprint);
            writer.WriteStartArray("outputs");
            foreach (var output in document.Outputs)
            {
                writer.WriteStartObject();
                writer.WriteString("filename", output.Filename);
                writer.WriteNumber("length", output.Length);
                writer.WriteString("sha256", output.Sha256);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
        }
        if (stream.Length > MaximumBytes)
            throw new InvalidDataException("source-exact installed state exceeds its byte bound");
        return stream.ToArray();
    }

    internal static SourceExactInstalledStateDocument Parse(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length == 0 || bytes.Length > MaximumBytes)
            throw new InvalidDataException("source-exact installed state has an invalid byte length");
        try
        {
            using var json = JsonDocument.Parse(bytes.ToArray(), new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 8
            });
            var root = json.RootElement;
            RequireObjectProperties(root,
                "schema_version", "authority", "mod_id", "workshop_id",
                "source_commit", "origin_release_tag", "container_release_id",
                "asset_id", "asset_filename", "asset_length", "asset_sha256",
                "output_fingerprint", "outputs");
            var outputElement = root.GetProperty("outputs");
            if (outputElement.ValueKind != JsonValueKind.Array ||
                outputElement.GetArrayLength() >= SourceExactZipStager.MaximumEntries)
                throw new InvalidDataException("source-exact installed output set is invalid");
            var outputs = new List<SourceExactInstalledOutput>(outputElement.GetArrayLength());
            foreach (var row in outputElement.EnumerateArray())
            {
                RequireObjectProperties(row, "filename", "length", "sha256");
                outputs.Add(new SourceExactInstalledOutput(
                    RequiredString(row, "filename"),
                    RequiredInt64(row, "length"),
                    RequiredString(row, "sha256")));
            }
            var document = new SourceExactInstalledStateDocument(
                RequiredInt32(root, "schema_version"),
                RequiredString(root, "authority"),
                RequiredString(root, "mod_id"),
                RequiredString(root, "workshop_id"),
                RequiredString(root, "source_commit"),
                RequiredString(root, "origin_release_tag"),
                RequiredInt64(root, "container_release_id"),
                RequiredInt64(root, "asset_id"),
                RequiredString(root, "asset_filename"),
                RequiredInt64(root, "asset_length"),
                RequiredString(root, "asset_sha256"),
                RequiredString(root, "output_fingerprint"),
                outputs.AsReadOnly());
            Validate(document);
            return document;
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("source-exact installed state is malformed", ex);
        }
    }

    internal static string Sha256(ReadOnlySpan<byte> bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    internal static void RequireSnapshotBinding(
        SourceExactInstalledStateDocument document,
        ExactDirectorySnapshot snapshot)
    {
        Validate(document);
        if (snapshot.Files.Count != document.Outputs.Count + 2 ||
            snapshot.Files.Count(row => row.Name == Filename) != 1 ||
            snapshot.Files.Count(row =>
                row.Name == SourceExactZipStager.VersionMarkerFilename) != 1)
            throw new InvalidDataException(
                "source-exact snapshot membership differs from installed-state authority");
        var actual = snapshot.Files
            .Where(row => row.Name != Filename &&
                row.Name != SourceExactZipStager.VersionMarkerFilename)
            .Select(row => new SourceExactInstalledOutput(
                row.Name,
                row.Length,
                row.Sha256))
            .ToArray();
        if (!actual.SequenceEqual(document.Outputs))
            throw new InvalidDataException(
                "source-exact installed output map differs from the physical snapshot");
    }

    private static void Validate(SourceExactInstalledStateDocument document)
    {
        if (document.SchemaVersion != SchemaVersion || document.Authority != Authority)
            throw new InvalidDataException("source-exact installed authority/schema is invalid");
        if (string.IsNullOrWhiteSpace(document.ModId) || document.ModId.Length > 64 ||
            string.IsNullOrWhiteSpace(document.WorkshopId) || document.WorkshopId.Length > 32 ||
            !document.WorkshopId.All(char.IsAsciiDigit) ||
            !Commit.IsMatch(document.SourceCommit) ||
            string.IsNullOrWhiteSpace(document.OriginReleaseTag) ||
            document.OriginReleaseTag.Length > 128 ||
            document.ContainerReleaseId <= 0 || document.AssetId <= 0 ||
            string.IsNullOrWhiteSpace(document.AssetFilename) || document.AssetFilename.Length > 128 ||
            document.AssetLength <= 0 ||
            !LowerSha256.IsMatch(document.AssetSha256) ||
            !LowerSha256.IsMatch(document.OutputFingerprint))
            throw new InvalidDataException("source-exact installed identity is invalid");
        if (document.Outputs.Count == 0 ||
            document.Outputs.Count >= SourceExactZipStager.MaximumEntries)
            throw new InvalidDataException("source-exact installed output count is invalid");
        string? previous = null;
        var insensitive = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long aggregate = 0;
        foreach (var output in document.Outputs)
        {
            if (!SourceExactTransactionFileSystem.SafeLeaf(output.Filename) || output.Length <= 0 ||
                output.Length > SourceExactZipStager.MaximumOutputBytes ||
                !LowerSha256.IsMatch(output.Sha256) ||
                output.Filename.Equals(Filename, StringComparison.OrdinalIgnoreCase) ||
                output.Filename.Equals(
                    SourceExactZipStager.VersionMarkerFilename,
                    StringComparison.OrdinalIgnoreCase) ||
                (previous is not null && StringComparer.Ordinal.Compare(previous, output.Filename) >= 0) ||
                !insensitive.Add(output.Filename))
                throw new InvalidDataException("source-exact installed output row is invalid");
            aggregate = checked(aggregate + output.Length);
            if (aggregate > SourceExactZipStager.MaximumAggregateOutputBytes)
                throw new InvalidDataException("source-exact installed outputs exceed aggregate bound");
            previous = output.Filename;
        }
        var fingerprintRows = document.Outputs
            .Select(output => new RecoveryOutputFile(
                output.Filename,
                output.Length,
                output.Sha256,
                ""))
            .ToArray();
        var computed = RecoveryRecordContract.ComputeOutputFingerprint(fingerprintRows);
        if (!CryptographicOperations.FixedTimeEquals(
                Convert.FromHexString(document.OutputFingerprint),
                Convert.FromHexString(computed)))
            throw new InvalidDataException(
                "source-exact installed output fingerprint is self-inconsistent");
    }

    private static void RequireObjectProperties(JsonElement element, params string[] expected)
    {
        if (element.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("source-exact installed state object is missing");
        var properties = element.EnumerateObject().Select(property => property.Name).ToArray();
        if (properties.Length != expected.Length ||
            properties.Distinct(StringComparer.Ordinal).Count() != properties.Length ||
            !properties.OrderBy(value => value, StringComparer.Ordinal)
                .SequenceEqual(expected.OrderBy(value => value, StringComparer.Ordinal), StringComparer.Ordinal))
            throw new InvalidDataException("source-exact installed state properties are missing, duplicated, or unknown");
    }

    private static string RequiredString(JsonElement element, string name)
    {
        var value = element.GetProperty(name);
        if (value.ValueKind != JsonValueKind.String)
            throw new InvalidDataException($"source-exact installed field '{name}' is not a string");
        return value.GetString()!;
    }

    private static long RequiredInt64(JsonElement element, string name)
    {
        var value = element.GetProperty(name);
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt64(out var result))
            throw new InvalidDataException($"source-exact installed field '{name}' is not an integer");
        return result;
    }

    private static int RequiredInt32(JsonElement element, string name)
    {
        var value = RequiredInt64(element, name);
        if (value < int.MinValue || value > int.MaxValue)
            throw new InvalidDataException($"source-exact installed field '{name}' is out of range");
        return (int)value;
    }
}

internal sealed record SourceExactInstalledStateDocument(
    int SchemaVersion,
    string Authority,
    string ModId,
    string WorkshopId,
    string SourceCommit,
    string OriginReleaseTag,
    long ContainerReleaseId,
    long AssetId,
    string AssetFilename,
    long AssetLength,
    string AssetSha256,
    string OutputFingerprint,
    IReadOnlyList<SourceExactInstalledOutput> Outputs);

internal sealed record SourceExactInstalledOutput(
    string Filename,
    long Length,
    string Sha256);
