using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using VT2ModUpdater.Models;

namespace VT2ModUpdater.Services;

/// <summary>
/// Reads only the source-exact recovery children from one schema-2 daily
/// manifest. Legacy rows remain representable but can never become a recovery
/// candidate through a weaker fallback.
/// </summary>
internal static class RecoveryManifestContract
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly Regex ReleaseTagPattern = new(
        "\\A[A-Za-z0-9][A-Za-z0-9._-]{0,127}\\z",
        RegexOptions.CultureInvariant);
    private static readonly Regex ModIdPattern = new(
        "\\A[A-Za-z0-9][A-Za-z0-9_-]*\\z",
        RegexOptions.CultureInvariant);
    private static readonly Regex ManifestTimestampPattern = new(
        "\\A[0-9]{4}-(0[1-9]|1[0-2])-(0[1-9]|[12][0-9]|3[01])T" +
        "([01][0-9]|2[0-3]):[0-5][0-9]:[0-5][0-9]\\.[0-9]{7}Z\\z",
        RegexOptions.CultureInvariant);
    private static readonly Regex AuthorizationTimestampPattern = new(
        "\\A[0-9]{4}-(0[1-9]|1[0-2])-(0[1-9]|[12][0-9]|3[01])T" +
        "([01][0-9]|2[0-3]):[0-5][0-9]:[0-5][0-9]Z\\z",
        RegexOptions.CultureInvariant);
    private static readonly Regex QaCheckPathPattern = new(
        "\\A/Ensrick/vermintide-2-tweaker/actions/runs/[1-9][0-9]*" +
        "(/job/[1-9][0-9]*)?\\z",
        RegexOptions.CultureInvariant);

    private static readonly string[] RootProperties =
        ["manifest_schema", "release_tag", "published_at", "mods"];
    private static readonly string[] RecoveryRowProperties =
    [
        "mod_id", "friendly_name", "workshop_id", "version", "asset_filename",
        "sha256", "visibility", "source_commit", "source_state", "bundle_authority",
        "builder", "root_bundle", "descriptor_name", "bundle_files", "recovery",
        "publication_authorization"
    ];
    private static readonly string[] BuilderProperties = ["name", "version"];
    private static readonly string[] BundleFileProperties = ["filename", "sha256"];
    private static readonly string[] AuthorizationProperties =
    [
        "mode", "source_commit", "checked_at_utc", "default_branch",
        "default_branch_commit", "merged_pr_number", "qa_check", "qa_check_url",
        "qa_completed_at_utc"
    ];

    public static RecoveryManifestScan ParseAndValidate(
        ReadOnlyMemory<byte> utf8Json,
        string expectedReleaseTag,
        int maximumRows,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(expectedReleaseTag);
        if (maximumRows <= 0)
            throw new ArgumentOutOfRangeException(nameof(maximumRows));

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var jsonPayload = HasUtf8Bom(utf8Json.Span)
                ? utf8Json[3..]
                : utf8Json;
            PreflightRowBound(jsonPayload.Span, maximumRows, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            using var document = JsonDocument.Parse(jsonPayload, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 40
            });
            cancellationToken.ThrowIfCancellationRequested();

            var root = CheckedObject.Read(document.RootElement, "manifest");
            var isSchemaTwo = root.TryGet("manifest_schema", out var schemaElement) &&
                IsExactInteger(schemaElement, 2);

            var mods = RequireArray(root.Require("mods"), "manifest.mods");
            var rowCount = mods.GetArrayLength();
            if (rowCount < 1)
                throw Error("manifest.mods must contain at least one row");
            if (rowCount > maximumRows)
                throw new RecoveryManifestBoundException(
                    $"manifest.mods exceeds the {maximumRows}-row remaining bound");

            var rows = new CheckedObject[rowCount];
            var hasRecovery = false;
            var rowIndex = 0;
            foreach (var rowElement in mods.EnumerateArray())
            {
                cancellationToken.ThrowIfCancellationRequested();
                var row = CheckedObject.Read(rowElement, $"manifest.mods[{rowIndex}]");
                rows[rowIndex] = row;
                row.RejectCaseVariant("recovery", $"manifest.mods[{rowIndex}]");
                row.RejectCaseVariant("bundle_authority", $"manifest.mods[{rowIndex}]");
                if (row.TryGet("recovery", out var recoveryElement))
                {
                    if (recoveryElement.ValueKind == JsonValueKind.Null)
                        throw Error($"manifest.mods[{rowIndex}].recovery must not be null");
                    hasRecovery = true;
                }
                else if (row.TryGet("bundle_authority", out var authorityElement))
                {
                    var authority = ReadCanonicalString(
                        authorityElement,
                        $"manifest.mods[{rowIndex}].bundle_authority",
                        16);
                    if (authority is not ("tracked" or "receipt"))
                    {
                        throw Error(
                            $"manifest.mods[{rowIndex}].bundle_authority is unsupported");
                    }
                    if (authority == "receipt")
                    {
                        throw Error(
                            $"manifest.mods[{rowIndex}] receipt authority requires recovery");
                    }
                }
                rowIndex++;
            }

            string? releaseTag = null;
            if (isSchemaTwo)
            {
                root.RequireExactProperties("manifest", RootProperties);
                releaseTag = ReadCanonicalString(
                    root.Require("release_tag"), "manifest.release_tag", 128);
                if (!ReleaseTagPattern.IsMatch(releaseTag))
                    throw Error("manifest.release_tag is not canonical");
                _ = ReadManifestTimestamp(
                    root.Require("published_at"), "manifest.published_at");
            }

            // A manifest with no recovery child cannot become source-exact.
            // Count its rows, but do not let known historical parent drift
            // (including overwritten daily tags) block a later valid record.
            if (!hasRecovery)
            {
                return new RecoveryManifestScan(
                    rowCount,
                    Array.Empty<ValidatedRecoveryRecord>());
            }
            if (!isSchemaTwo)
                throw Error("a non-schema-2 manifest cannot carry a recovery child");

            RequireEqual(releaseTag!, expectedReleaseTag,
                "manifest.release_tag/release.tag_name");

            var recovered = new List<ValidatedRecoveryRecord>(rowCount);
            var ordinalModIds = new HashSet<string>(StringComparer.Ordinal);
            var foldedModIds = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var index = 0;
            foreach (var row in rows)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var path = $"manifest.mods[{index}]";
                var modId = ReadModId(row.Require("mod_id"), $"{path}.mod_id");
                if (!ordinalModIds.Add(modId))
                    throw Error($"manifest.mods contains duplicate mod_id '{modId}'");
                if (foldedModIds.TryGetValue(modId, out var prior))
                {
                    throw Error(
                        $"manifest.mods contains case-colliding mod_ids '{prior}' and '{modId}'");
                }
                foldedModIds.Add(modId, modId);

                if (!row.TryGet("recovery", out var recoveryElement) ||
                    recoveryElement.ValueKind == JsonValueKind.Null)
                {
                    index++;
                    continue;
                }

                row.RequireExactProperties(path, RecoveryRowProperties);
                ValidateRecoveryParentRow(row, path);
                var binding = ParseBinding(row, path, modId);
                var recoveryJson = recoveryElement.GetRawText();
                try
                {
                    recovered.Add(RecoveryRecordContract.ParseAndValidate(
                        recoveryJson,
                        binding));
                }
                catch (RecoveryRecordValidationException ex)
                {
                    throw Error($"{path}.recovery is invalid: {ex.Message}", ex);
                }
                index++;
            }

            return new RecoveryManifestScan(
                rowCount,
                Array.AsReadOnly(recovered.ToArray()));
        }
        catch (RecoveryManifestValidationException)
        {
            throw;
        }
        catch (RecoveryManifestBoundException)
        {
            throw;
        }
        catch (JsonException ex)
        {
            throw Error($"manifest JSON is malformed: {ex.Message}", ex);
        }
        catch (DecoderFallbackException ex)
        {
            throw Error("manifest JSON contains invalid UTF-8", ex);
        }
        catch (EncoderFallbackException ex)
        {
            throw Error("manifest JSON contains invalid Unicode", ex);
        }
    }

    private static void PreflightRowBound(
        ReadOnlySpan<byte> utf8Json,
        int maximumRows,
        CancellationToken cancellationToken)
    {
        var reader = new Utf8JsonReader(utf8Json, new JsonReaderOptions
        {
            AllowTrailingCommas = false,
            CommentHandling = JsonCommentHandling.Disallow,
            MaxDepth = 40
        });
        if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
            throw Error("manifest must be a JSON object");

        var sawMods = false;
        while (reader.Read())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (reader.TokenType == JsonTokenType.EndObject)
            {
                if (reader.Read())
                    throw Error("manifest has trailing JSON content");
                return;
            }
            if (reader.TokenType != JsonTokenType.PropertyName)
                throw Error("manifest has malformed object grammar");

            var isMods = reader.ValueTextEquals("mods"u8) ||
                string.Equals(reader.GetString(), "mods", StringComparison.OrdinalIgnoreCase);
            if (!reader.Read())
                throw Error("manifest has an incomplete property");
            if (!isMods)
            {
                SkipCurrentJsonValue(ref reader, cancellationToken);
                continue;
            }
            if (sawMods)
                throw Error("manifest repeats mods metadata");
            sawMods = true;
            if (reader.TokenType != JsonTokenType.StartArray)
                throw Error("manifest.mods must be a JSON array");

            var rowCount = 0;
            while (reader.Read())
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (reader.TokenType == JsonTokenType.EndArray)
                    break;
                rowCount++;
                if (rowCount > maximumRows)
                {
                    throw new RecoveryManifestBoundException(
                        $"manifest.mods exceeds the {maximumRows}-row remaining bound");
                }

                // Reject the first excess row before traversing or allocating
                // a DOM node for it. Rows within budget remain structurally
                // skipped here and receive full semantic validation later.
                SkipCurrentJsonValue(ref reader, cancellationToken);
            }
        }

        throw Error("manifest JSON object is incomplete");
    }

    private static void SkipCurrentJsonValue(
        ref Utf8JsonReader reader,
        CancellationToken cancellationToken)
    {
        if (reader.TokenType is not (JsonTokenType.StartArray or JsonTokenType.StartObject))
            return;

        var openContainers = 1;
        while (openContainers > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!reader.Read())
                throw Error("manifest JSON value is incomplete");
            if (reader.TokenType is JsonTokenType.StartArray or JsonTokenType.StartObject)
                openContainers++;
            else if (reader.TokenType is JsonTokenType.EndArray or JsonTokenType.EndObject)
                openContainers--;
        }
    }

    private static RecoveryManifestBinding ParseBinding(
        CheckedObject row,
        string path,
        string modId)
    {
        var builderObject = CheckedObject.Read(row.Require("builder"), $"{path}.builder");
        builderObject.RequireExactProperties($"{path}.builder", BuilderProperties);
        var bundleFilesElement = RequireArray(
            row.Require("bundle_files"), $"{path}.bundle_files");
        var bundleFileCount = bundleFilesElement.GetArrayLength();
        if (bundleFileCount is < 1 or > RecoveryRecordContract.MaxOutputFiles)
        {
            throw Error(
                $"{path}.bundle_files must contain 1..{RecoveryRecordContract.MaxOutputFiles} rows");
        }

        var bundleFiles = new RecoveryManifestBundleFile[bundleFileCount];
        var index = 0;
        foreach (var bundleFileElement in bundleFilesElement.EnumerateArray())
        {
            var bundlePath = $"{path}.bundle_files[{index}]";
            var bundleFile = CheckedObject.Read(bundleFileElement, bundlePath);
            bundleFile.RequireExactProperties(bundlePath, BundleFileProperties);
            bundleFiles[index] = new RecoveryManifestBundleFile(
                ReadCanonicalString(
                    bundleFile.Require("filename"), $"{bundlePath}.filename", 256),
                ReadCanonicalString(
                    bundleFile.Require("sha256"), $"{bundlePath}.sha256", 64));
            index++;
        }

        return new RecoveryManifestBinding(
            modId,
            ReadCanonicalString(row.Require("workshop_id"), $"{path}.workshop_id", 20),
            ReadCanonicalString(row.Require("version"), $"{path}.version", 128),
            ReadCanonicalString(
                row.Require("asset_filename"), $"{path}.asset_filename", 256),
            ReadCanonicalString(row.Require("sha256"), $"{path}.sha256", 64),
            ReadCanonicalString(
                row.Require("source_commit"), $"{path}.source_commit", 40),
            ReadCanonicalString(
                row.Require("source_state"), $"{path}.source_state", 32),
            ReadCanonicalString(
                builderObject.Require("name"), $"{path}.builder.name", 64),
            ReadCanonicalString(
                builderObject.Require("version"), $"{path}.builder.version", 128),
            ReadCanonicalString(
                row.Require("bundle_authority"), $"{path}.bundle_authority", 16),
            ReadCanonicalString(
                row.Require("root_bundle"), $"{path}.root_bundle", 64),
            ReadCanonicalString(
                row.Require("descriptor_name"), $"{path}.descriptor_name", 256),
            Array.AsReadOnly(bundleFiles));
    }

    private static void ValidateRecoveryParentRow(CheckedObject row, string path)
    {
        _ = ReadCanonicalString(row.Require("friendly_name"), $"{path}.friendly_name", 256);
        var visibility = ReadCanonicalString(
            row.Require("visibility"), $"{path}.visibility", 32);
        if (visibility is not ("public" or "friends_only" or "private"))
            throw Error($"{path}.visibility is unsupported");

        var authority = ReadCanonicalString(
            row.Require("bundle_authority"), $"{path}.bundle_authority", 16);
        if (authority is not ("tracked" or "receipt"))
            throw Error($"{path}.bundle_authority must be exactly tracked or receipt");

        var sourceCommit = ReadCanonicalString(
            row.Require("source_commit"), $"{path}.source_commit", 40);
        ValidateAuthorization(
            CheckedObject.Read(
                row.Require("publication_authorization"),
                $"{path}.publication_authorization"),
            $"{path}.publication_authorization",
            sourceCommit);
    }

    private static void ValidateAuthorization(
        CheckedObject authorization,
        string path,
        string sourceCommit)
    {
        authorization.RequireExactProperties(path, AuthorizationProperties);
        RequireEqual(
            ReadCanonicalString(authorization.Require("mode"), $"{path}.mode", 16),
            "hosted_qa",
            $"{path}.mode");
        RequireEqual(
            ReadCanonicalString(
                authorization.Require("source_commit"), $"{path}.source_commit", 40),
            sourceCommit,
            $"{path}.source_commit/manifest.source_commit");
        _ = ReadAuthorizationTimestamp(
            authorization.Require("checked_at_utc"), $"{path}.checked_at_utc");
        var defaultBranch = ReadCanonicalString(
            authorization.Require("default_branch"), $"{path}.default_branch", 256);
        if (defaultBranch.Any(char.IsWhiteSpace))
            throw Error($"{path}.default_branch contains whitespace");
        RequireEqual(
            ReadCanonicalString(
                authorization.Require("default_branch_commit"),
                $"{path}.default_branch_commit",
                40),
            sourceCommit,
            $"{path}.default_branch_commit/manifest.source_commit");
        _ = ReadCanonicalPositiveInt64(
            authorization.Require("merged_pr_number"), $"{path}.merged_pr_number");
        RequireEqual(
            ReadCanonicalString(
                authorization.Require("qa_check"), $"{path}.qa_check", 32),
            "qa-gate",
            $"{path}.qa_check");
        ValidateQaCheckUrl(
            ReadCanonicalString(
                authorization.Require("qa_check_url"), $"{path}.qa_check_url", 2048),
            $"{path}.qa_check_url");
        _ = ReadAuthorizationTimestamp(
            authorization.Require("qa_completed_at_utc"),
            $"{path}.qa_completed_at_utc");
    }

    private static void ValidateQaCheckUrl(string value, string path)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.Ordinal) ||
            uri.Port != 443 ||
            !string.Equals(uri.Host, "github.com", StringComparison.OrdinalIgnoreCase) ||
            !QaCheckPathPattern.IsMatch(uri.AbsolutePath) ||
            !string.IsNullOrEmpty(uri.UserInfo) ||
            !string.IsNullOrEmpty(uri.Fragment) ||
            !string.IsNullOrEmpty(uri.Query))
        {
            throw Error($"{path} is not an exact repository hosted-QA URL");
        }
    }

    private static string ReadModId(JsonElement element, string path)
    {
        var value = ReadCanonicalString(element, path, 128);
        if (!ModIdPattern.IsMatch(value))
            throw Error($"{path} is not canonical");
        return value;
    }

    private static string ReadCanonicalString(
        JsonElement element,
        string path,
        int maximumUtf8Bytes)
    {
        if (element.ValueKind != JsonValueKind.String)
            throw Error($"{path} must be a JSON string");
        var value = element.GetString() ?? throw Error($"{path} must not be null");
        if (string.IsNullOrWhiteSpace(value) || value.Length > maximumUtf8Bytes ||
            !string.Equals(value, value.Trim(), StringComparison.Ordinal) ||
            value.Any(char.IsControl) ||
            StrictUtf8.GetByteCount(value) > maximumUtf8Bytes)
        {
            throw Error(
                $"{path} is empty, non-canonical, or exceeds its {maximumUtf8Bytes}-byte bound");
        }
        return value;
    }

    private static DateTimeOffset ReadManifestTimestamp(
        JsonElement element,
        string path)
    {
        var value = ReadCanonicalString(element, path, 64);
        if (!ManifestTimestampPattern.IsMatch(value) ||
            !DateTimeOffset.TryParseExact(
                value,
                "yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'",
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out var parsed) ||
            parsed.Offset != TimeSpan.Zero)
        {
            throw Error($"{path} must use the producer's seven-fraction UTC grammar");
        }
        return parsed;
    }

    private static DateTimeOffset ReadAuthorizationTimestamp(
        JsonElement element,
        string path)
    {
        var value = ReadCanonicalString(element, path, 64);
        if (!AuthorizationTimestampPattern.IsMatch(value) ||
            !DateTimeOffset.TryParseExact(
                value,
                "yyyy-MM-dd'T'HH:mm:ss'Z'",
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out var parsed) ||
            parsed.Offset != TimeSpan.Zero)
        {
            throw Error($"{path} must use the authorization's whole-second UTC grammar");
        }
        return parsed;
    }

    private static long ReadCanonicalPositiveInt64(JsonElement element, string path)
    {
        if (element.ValueKind != JsonValueKind.Number)
            throw Error($"{path} must be a JSON integer number");
        var raw = element.GetRawText();
        if (raw.Length > 19 || raw.Length == 0 || raw[0] == '0' ||
            raw.Any(character => character is < '0' or > '9') ||
            !long.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out var value) ||
            value <= 0)
        {
            throw Error($"{path} must be a canonical positive Int64");
        }
        return value;
    }

    private static JsonElement RequireArray(JsonElement element, string path)
    {
        if (element.ValueKind != JsonValueKind.Array)
            throw Error($"{path} must be a JSON array");
        return element;
    }

    private static bool IsExactInteger(JsonElement element, int expected) =>
        element.ValueKind == JsonValueKind.Number &&
        string.Equals(
            element.GetRawText(),
            expected.ToString(CultureInfo.InvariantCulture),
            StringComparison.Ordinal);

    private static void RequireEqual(string actual, string expected, string path)
    {
        if (!string.Equals(actual, expected, StringComparison.Ordinal))
            throw Error($"{path} differs: expected '{expected}', got '{actual}'");
    }

    private static RecoveryManifestValidationException Error(
        string message,
        Exception? inner = null) => new(message, inner);

    private static bool HasUtf8Bom(ReadOnlySpan<byte> bytes) =>
        bytes.Length >= 3 && bytes[0] == 0xef && bytes[1] == 0xbb && bytes[2] == 0xbf;

    private sealed class CheckedObject
    {
        private readonly Dictionary<string, JsonElement> _properties;

        private CheckedObject(Dictionary<string, JsonElement> properties) =>
            _properties = properties;

        public JsonElement Require(string name) =>
            _properties.TryGetValue(name, out var value)
                ? value
                : throw Error($"JSON object is missing required property '{name}'");

        public bool TryGet(string name, out JsonElement value) =>
            _properties.TryGetValue(name, out value);

        public void RejectCaseVariant(string name, string path)
        {
            foreach (var actual in _properties.Keys)
            {
                if (!string.Equals(actual, name, StringComparison.Ordinal) &&
                    string.Equals(actual, name, StringComparison.OrdinalIgnoreCase))
                {
                    throw Error(
                        $"{path} contains wrong-case property '{actual}' for '{name}'");
                }
            }
        }

        public void RequireExactProperties(string path, IReadOnlyList<string> expectedNames)
        {
            if (_properties.Count != expectedNames.Count)
                ThrowPropertySetError(path, expectedNames);
            foreach (var expected in expectedNames)
            {
                if (!_properties.ContainsKey(expected))
                    ThrowPropertySetError(path, expectedNames);
            }
        }

        private void ThrowPropertySetError(string path, IReadOnlyList<string> expectedNames)
        {
            var expected = new HashSet<string>(expectedNames, StringComparer.Ordinal);
            var unsupported = _properties.Keys
                .Where(name => !expected.Contains(name))
                .OrderBy(name => name, StringComparer.Ordinal)
                .ToArray();
            var missing = expectedNames
                .Where(name => !_properties.ContainsKey(name))
                .ToArray();
            throw Error(
                $"{path} property set is not exact; missing=[{string.Join(",", missing)}], " +
                $"unsupported=[{string.Join(",", unsupported)}]");
        }

        public static CheckedObject Read(JsonElement element, string path)
        {
            if (element.ValueKind != JsonValueKind.Object)
                throw Error($"{path} must be a JSON object");

            var properties = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (!properties.TryAdd(property.Name, property.Value))
                    throw Error($"{path} contains duplicate property '{property.Name}'");
            }
            return new CheckedObject(properties);
        }
    }
}

internal sealed record RecoveryManifestScan(
    int RowCount,
    IReadOnlyList<ValidatedRecoveryRecord> RecoveryRecords);

internal sealed class RecoveryManifestValidationException : Exception
{
    public RecoveryManifestValidationException(string message, Exception? inner = null)
        : base(message, inner)
    {
    }
}

internal sealed class RecoveryManifestBoundException : Exception
{
    public RecoveryManifestBoundException(string message)
        : base(message)
    {
    }
}
