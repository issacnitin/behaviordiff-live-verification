using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace BehaviorDiff.Engine
{
    /// <summary>
    /// The one consumer-facing artifact. Engine details remain in DivergenceSet and FrontierReport;
    /// this command only projects their already-decided results into a stable member-level schema.
    /// </summary>
    internal static class FindingsCommand
    {
        private static readonly JsonSerializerOptions Json = new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        };

        internal static void WriteAnalyzed(
            string divergenceSetPath,
            string frontierReportPath,
            string output,
            int exitCode,
            string baseSha,
            string prSha,
            string mergeBaseSha)
        {
            using JsonDocument divergenceSet = JsonDocument.Parse(File.ReadAllText(divergenceSetPath));
            using JsonDocument frontierReport = JsonDocument.Parse(File.ReadAllText(frontierReportPath));

            JsonElement frontier = frontierReport.RootElement.GetProperty("frontier");
            JsonElement divergences = divergenceSet.RootElement.GetProperty("divergences");
            var nodes = frontier.EnumerateArray().ToList();
            var observations = divergences.EnumerateArray().ToList();

            var members = nodes
                .GroupBy(node => String(node, "methodFullName"), StringComparer.Ordinal)
                .OrderBy(group => String(group.First(), "attribution") == "UNEXPECTED" ? 0 : 1)
                .ThenByDescending(group => group.Count())
                .Select(group => DescribeMember(group, observations))
                .ToArray();

            int unexpectedMembers = members.Count(member => member.Attribution == "unexpected");
            int expectedMembers = members.Count(member => member.Attribution == "expected");
            int unexpectedCallSites = members.Where(member => member.Attribution == "unexpected").Sum(member => member.CallSiteCount);
            int expectedCallSites = members.Where(member => member.Attribution == "expected").Sum(member => member.CallSiteCount);

            var artifact = new
            {
                schema = "behaviordiff.findings/1",
                generatedUtc = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture),
                status = "analyzed",
                verdict = unexpectedMembers == 0 ? "clean" : "findings",
                isCleanResult = unexpectedMembers == 0,
                exitCode,
                exitReason = unexpectedMembers == 0 ? "analyzed_no_unexpected" : "unexpected_findings",
                refs = new { baseSha, prSha, mergeBaseSha },
                summary = new
                {
                    unexpectedMembers,
                    unexpectedCallSites,
                    expectedMembers,
                    expectedCallSites,
                    untestedMembers = members.Count(member => member.UntestedCallSiteCount > 0),
                },
                members,
            };

            Write(output, artifact);
        }

        /// <summary>
        /// Invalid runs intentionally have no members property. A consumer cannot deserialize a refusal
        /// as an analyzed result with an empty array; it must first choose the status arm of the schema.
        /// </summary>
        internal static void WriteInvalid(
            string output,
            string status,
            int exitCode,
            string reason,
            string? baseSha = null,
            string? prSha = null,
            string? mergeBaseSha = null)
        {
            if (status != "refused" && status != "failed")
            {
                throw new ArgumentException("Invalid findings status '" + status + "'.", nameof(status));
            }

            var artifact = new
            {
                schema = "behaviordiff.findings/1",
                generatedUtc = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture),
                status,
                verdict = "could_not_analyze",
                isCleanResult = false,
                exitCode,
                exitReason = status == "refused" ? "analysis_refused" : "analysis_failed",
                refs = new { baseSha, prSha, mergeBaseSha },
                refusal = new { reason },
            };

            Write(output, artifact);
        }

        private static FindingMember DescribeMember(
            IGrouping<string, JsonElement> group,
            IReadOnlyList<JsonElement> divergences)
        {
            JsonElement first = group.First();
            string? filePath = NullableString(first, "filePath");
            bool sourceGenerated = FrontierCommand.IsGeneratedSource(filePath);
            var evidence = divergences
                .Where(divergence => string.Equals(String(divergence, "methodFullName"), group.Key, StringComparison.Ordinal))
                .Select(divergence => new FindingEvidence
                {
                    TestId = String(divergence, "testId"),
                    Kind = String(divergence, "kind"),
                    Detail = String(divergence, "detail"),
                    DigestConfidence = String(divergence, "digestConfidence"),
                    BaseArgs = NullableString(divergence, "baseArgsRendered"),
                    PrArgs = NullableString(divergence, "prArgsRendered"),
                    BaseReturn = NullableString(divergence, "baseReturnRendered"),
                    PrReturn = NullableString(divergence, "prReturnRendered"),
                    BaseException = NullableString(divergence, "baseExceptionType"),
                    PrException = NullableString(divergence, "prExceptionType"),
                })
                .ToArray();

            return new FindingMember
            {
                MemberName = group.Key,
                Attribution = String(first, "attribution").ToLowerInvariant(),
                FilePath = filePath,
                Line = NullableInt(first, "line"),
                SourceGenerated = sourceGenerated,
                SourceGeneratedNote = sourceGenerated
                    ? "Generated source is not present in the git diff, so path attribution cannot classify it as edited."
                    : null,
                CallSiteCount = group.Count(),
                DistinctTestCount = group.Select(node => String(node, "testId")).Distinct(StringComparer.Ordinal).Count(),
                Verified = String(first, "classification") == "frontier",
                Symptoms = group.SelectMany(node => Strings(node, "symptoms")).Distinct(StringComparer.Ordinal).ToArray(),
                DowngradeReasons = group.SelectMany(node => Strings(node, "downgradeReasons")).Distinct(StringComparer.Ordinal).ToArray(),
                DescendantsCompared = group.Sum(node => Int(node, "descendantKeysCompared")),
                UntestedCallSiteCount = group.Count(node => Bool(node, "untested")),
                Evidence = evidence,
            };
        }

        private static void Write(string output, object artifact)
        {
            string fullPath = Path.GetFullPath(output);
            string? directory = Path.GetDirectoryName(fullPath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.WriteAllText(fullPath, JsonSerializer.Serialize(artifact, Json));
            Console.WriteLine("Findings written: " + fullPath);
        }

        private static string String(JsonElement element, string property) =>
            element.TryGetProperty(property, out JsonElement value) && value.ValueKind == JsonValueKind.String
                ? value.GetString() ?? string.Empty
                : string.Empty;

        private static string? NullableString(JsonElement element, string property) =>
            element.TryGetProperty(property, out JsonElement value) && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;

        private static int Int(JsonElement element, string property) =>
            NullableInt(element, property) ?? 0;

        private static int? NullableInt(JsonElement element, string property) =>
            element.TryGetProperty(property, out JsonElement value)
                && value.ValueKind == JsonValueKind.Number
                && value.TryGetInt32(out int number)
                    ? number
                    : null;

        private static bool Bool(JsonElement element, string property) =>
            element.TryGetProperty(property, out JsonElement value) && value.ValueKind == JsonValueKind.True;

        private static IEnumerable<string> Strings(JsonElement element, string property) =>
            element.TryGetProperty(property, out JsonElement values) && values.ValueKind == JsonValueKind.Array
                ? values.EnumerateArray().Select(value => value.GetString() ?? string.Empty)
                : Enumerable.Empty<string>();

        private sealed class FindingMember
        {
            public string MemberName { get; init; } = string.Empty;
            public string Attribution { get; init; } = string.Empty;
            public string? FilePath { get; init; }
            public int? Line { get; init; }
            public bool SourceGenerated { get; init; }
            public string? SourceGeneratedNote { get; init; }
            public int CallSiteCount { get; init; }
            public int DistinctTestCount { get; init; }
            public bool Verified { get; init; }
            public string[] Symptoms { get; init; } = Array.Empty<string>();
            public string[] DowngradeReasons { get; init; } = Array.Empty<string>();
            public int DescendantsCompared { get; init; }
            public int UntestedCallSiteCount { get; init; }
            public FindingEvidence[] Evidence { get; init; } = Array.Empty<FindingEvidence>();
        }

        private sealed class FindingEvidence
        {
            public string TestId { get; init; } = string.Empty;
            public string Kind { get; init; } = string.Empty;
            public string Detail { get; init; } = string.Empty;
            public string DigestConfidence { get; init; } = string.Empty;
            public string? BaseArgs { get; init; }
            public string? PrArgs { get; init; }
            public string? BaseReturn { get; init; }
            public string? PrReturn { get; init; }
            public string? BaseException { get; init; }
            public string? PrException { get; init; }
        }
    }
}