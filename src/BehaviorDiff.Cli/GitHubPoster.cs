using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace BehaviorDiff.Cli
{
    /// <summary>Thin GitHub transport. All behavior decisions already live in findings.json.</summary>
    internal sealed class GitHubPoster
    {
        // Current GitHub.com REST version documented on 2026-08-19.
        private const string ApiVersion = "2026-03-10";
        private readonly JsonSerializerOptions _json = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

        internal async Task PostAsync(JsonElement findings)
        {
            GitHubContext context = ReadContext();
            if (context.IsFork)
            {
                throw new CliException(
                    "FORK PULL REQUEST: GitHub gives pull_request workflows from forks a read-only GITHUB_TOKEN. "
                    + "BehaviorDiff analyzed the run but cannot post PR comments. No comment was posted; inspect "
                    + "findings.json in the workflow artifact instead.");
            }

            string token = RequiredEnvironment("GITHUB_TOKEN");
            string api = Environment.GetEnvironmentVariable("GITHUB_API_URL") ?? "https://api.github.com";
            string root = api.TrimEnd('/') + "/repos/" + context.Repository;

            using var client = new HttpClient();
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
            client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
            client.DefaultRequestHeaders.Add("X-GitHub-Api-Version", ApiVersion);
            client.DefaultRequestHeaders.UserAgent.ParseAdd("BehaviorDiff/1.0");

            List<ExistingComment> issueComments = await ListComments(
                client,
                root + "/issues/" + context.PullRequestNumber + "/comments?per_page=100",
                "body").ConfigureAwait(false);
            List<ExistingComment> reviewComments = await ListComments(
                client,
                root + "/pulls/" + context.PullRequestNumber + "/comments?per_page=100",
                "body").ConfigureAwait(false);

            var anchorFailures = new List<string>();
            if (String(findings, "status") == "analyzed"
                && findings.TryGetProperty("members", out JsonElement members))
            {
                foreach (JsonElement member in members.EnumerateArray()
                    .Where(member => String(member, "attribution") == "unexpected"))
                {
                    string? filePath = NullableString(member, "filePath");
                    int? line = NullableInt(member, "line");
                    if (filePath is null || line is null || line <= 0 || Bool(member, "sourceGenerated"))
                    {
                        continue;
                    }

                    string marker = "<!-- behaviordiff:github:pr:" + context.PullRequestNumber + ":member:"
                        + MemberKey(String(member, "memberName")) + " -->";
                    try
                    {
                        await UpsertReviewComment(
                            client,
                            root,
                            context,
                            reviewComments,
                            marker,
                            RenderMember(member, marker),
                            filePath,
                            line.Value).ConfigureAwait(false);
                    }
                    catch (GitHubApiException ex)
                    {
                        // GitHub review comments can only target a line in the unified PR diff. An
                        // UNEXPECTED file is normally absent from that diff by definition. Preserve the
                        // platform refusal in the summary rather than suppressing the finding or the post.
                        anchorFailures.Add(filePath + ":" + line.Value + " - GitHub REST "
                            + (int)ex.StatusCode + " " + ex.StatusCode + ": " + ex.Response);
                    }
                }
            }

            string summaryMarker = "<!-- behaviordiff:github:pr:" + context.PullRequestNumber + ":summary -->";
            await UpsertIssueComment(
                client,
                root,
                context.PullRequestNumber,
                issueComments,
                summaryMarker,
                RenderSummary(findings, summaryMarker, anchorFailures)).ConfigureAwait(false);

            foreach (string failure in anchorFailures)
            {
                Console.WriteLine("  GitHub line comment not created: " + failure);
            }
        }

        private async Task<List<ExistingComment>> ListComments(
            HttpClient client,
            string url,
            string bodyProperty)
        {
            using JsonDocument response = await Send(client, HttpMethod.Get, url, body: null).ConfigureAwait(false);
            var comments = new List<ExistingComment>();
            if (response.RootElement.ValueKind != JsonValueKind.Array)
            {
                return comments;
            }

            foreach (JsonElement comment in response.RootElement.EnumerateArray())
            {
                comments.Add(new ExistingComment(
                    comment.GetProperty("id").GetInt64(),
                    String(comment, bodyProperty)));
            }

            return comments;
        }

        private async Task UpsertIssueComment(
            HttpClient client,
            string root,
            int pullRequestNumber,
            IReadOnlyList<ExistingComment> existing,
            string marker,
            string body)
        {
            ExistingComment? match = existing.FirstOrDefault(comment => comment.Body.Contains(marker, StringComparison.Ordinal));
            if (match is not null)
            {
                using JsonDocument _ = await Send(
                    client,
                    HttpMethod.Patch,
                    root + "/issues/comments/" + match.Id,
                    new { body }).ConfigureAwait(false);
                Console.WriteLine("  updated GitHub PR summary comment " + match.Id);
                return;
            }

            using JsonDocument created = await Send(
                client,
                HttpMethod.Post,
                root + "/issues/" + pullRequestNumber + "/comments",
                new { body }).ConfigureAwait(false);
            Console.WriteLine("  created GitHub PR summary comment " + created.RootElement.GetProperty("id").GetInt64());
        }

        private async Task UpsertReviewComment(
            HttpClient client,
            string root,
            GitHubContext context,
            IReadOnlyList<ExistingComment> existing,
            string marker,
            string body,
            string filePath,
            int line)
        {
            ExistingComment? match = existing.FirstOrDefault(comment => comment.Body.Contains(marker, StringComparison.Ordinal));
            if (match is not null)
            {
                using JsonDocument _ = await Send(
                    client,
                    HttpMethod.Patch,
                    root + "/pulls/comments/" + match.Id,
                    new { body }).ConfigureAwait(false);
                Console.WriteLine("  updated GitHub review comment " + match.Id);
                return;
            }

            using JsonDocument created = await Send(
                client,
                HttpMethod.Post,
                root + "/pulls/" + context.PullRequestNumber + "/comments",
                new
                {
                    body,
                    commit_id = context.HeadSha,
                    path = filePath,
                    line,
                    side = "RIGHT",
                }).ConfigureAwait(false);
            Console.WriteLine("  created GitHub review comment " + created.RootElement.GetProperty("id").GetInt64());
        }

        private async Task<JsonDocument> Send(HttpClient client, HttpMethod method, string url, object? body)
        {
            try
            {
                using var request = new HttpRequestMessage(method, url);
                if (body is not null)
                {
                    request.Content = new StringContent(JsonSerializer.Serialize(body, _json), Encoding.UTF8, "application/json");
                }

                using HttpResponseMessage response = await client.SendAsync(request).ConfigureAwait(false);
                string content = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    throw new GitHubApiException(response.StatusCode, Truncate(content));
                }

                return JsonDocument.Parse(content.Length == 0 ? "{}" : content);
            }
            catch (HttpRequestException ex)
            {
                throw new CliException("GitHub REST request failed: " + ex.Message);
            }
        }

        private static GitHubContext ReadContext()
        {
            string eventPath = RequiredEnvironment("GITHUB_EVENT_PATH");
            try
            {
                using JsonDocument document = JsonDocument.Parse(File.ReadAllText(eventPath));
                JsonElement root = document.RootElement;
                JsonElement pullRequest = root.GetProperty("pull_request");
                JsonElement head = pullRequest.GetProperty("head");
                JsonElement headRepository = head.GetProperty("repo");
                JsonElement baseRepository = pullRequest.GetProperty("base").GetProperty("repo");
                string headFullName = String(headRepository, "full_name");
                string baseFullName = String(baseRepository, "full_name");
                bool fork = Bool(headRepository, "fork")
                    || !string.Equals(headFullName, baseFullName, StringComparison.OrdinalIgnoreCase);

                return new GitHubContext(
                    root.GetProperty("number").GetInt32(),
                    RequiredEnvironment("GITHUB_REPOSITORY"),
                    String(head, "sha"),
                    fork);
            }
            catch (IOException ex)
            {
                throw new CliException("Could not read GITHUB_EVENT_PATH: " + ex.Message);
            }
            catch (JsonException ex)
            {
                throw new CliException("GITHUB_EVENT_PATH is malformed JSON: " + ex.Message);
            }
            catch (InvalidOperationException ex)
            {
                throw new CliException("GITHUB_EVENT_PATH is not a pull_request payload: " + ex.Message);
            }
            catch (KeyNotFoundException ex)
            {
                throw new CliException("GITHUB_EVENT_PATH is missing pull_request metadata: " + ex.Message);
            }
        }

        private static string RenderSummary(
            JsonElement findings,
            string marker,
            IReadOnlyList<string> anchorFailures)
        {
            var builder = new StringBuilder();
            string status = String(findings, "status");
            if (status != "analyzed")
            {
                string reason = findings.TryGetProperty("refusal", out JsonElement refusal)
                    ? String(refusal, "reason")
                    : "No reason was recorded.";
                builder.AppendLine("## BehaviorDiff: analysis could not complete");
                builder.AppendLine();
                builder.AppendLine("**No safety verdict was produced.** This is not a clean result.");
                builder.AppendLine();
                builder.AppendLine("> " + reason.Replace("\n", "\n> "));
                builder.AppendLine();
                builder.Append(marker);
                return builder.ToString();
            }

            JsonElement summary = findings.GetProperty("summary");
            builder.AppendLine("## BehaviorDiff runtime analysis");
            builder.AppendLine();
            AppendCoverage(builder, findings);
            builder.AppendLine();

            int unexpectedMembers = Int(summary, "unexpectedMembers");
            if (unexpectedMembers == 0)
            {
                builder.AppendLine("**No unexpected behavior changes across " + Int(summary, "editedFiles")
                    + " edited files (" + Int(summary, "tracedMembers")
                    + (Int(summary, "tracedMembers") == 1 ? " member, " : " members, ")
                    + Int(summary, "observedCallSites")
                    + (Int(summary, "observedCallSites") == 1 ? " call site observed).**" : " call sites observed).**"));
            }
            else
            {
                builder.AppendLine("**UNEXPECTED: " + unexpectedMembers + " member(s), across "
                    + Int(summary, "unexpectedCallSites") + " call site(s).**");
                builder.AppendLine();
                builder.AppendLine("Unexpected means runtime behavior changed in a file the PR did not modify. That is the point of this analysis.");
                builder.AppendLine();
                AppendMembers(builder, findings, "unexpected", "Unexpected members");
            }

            builder.AppendLine();
            builder.AppendLine("**EXPECTED: " + Int(summary, "expectedMembers") + " member(s), across "
                + Int(summary, "expectedCallSites") + " call site(s).**");
            AppendMembers(builder, findings, "expected", "Expected members");

            if (anchorFailures.Count > 0)
            {
                builder.AppendLine();
                builder.AppendLine("### GitHub line-comment limitations");
                builder.AppendLine("GitHub only accepts review comments on lines present in the PR diff. "
                    + "These unexpected files were resolved locally but GitHub rejected their line anchors:");
                foreach (string failure in anchorFailures)
                {
                    builder.AppendLine("- " + Escape(failure));
                }
            }

            builder.AppendLine();
            builder.Append(marker);
            return builder.ToString();
        }

        private static void AppendCoverage(StringBuilder builder, JsonElement findings)
        {
            JsonElement coverage = findings.GetProperty("coverage");
            JsonElement summary = coverage.GetProperty("summary");
            builder.AppendLine("### Edited-code coverage");
            builder.AppendLine("**" + Int(summary, "exercisedEditedFiles") + " of "
                + Int(summary, "editedFiles") + " edited files were exercised by tests.**");
            int members = Int(summary, "tracedMembers");
            int callSites = Int(summary, "observedCallSites");
            int calls = Int(summary, "totalCallCount");
            builder.AppendLine(members + (members == 1 ? " member, " : " members, ")
                + callSites + (callSites == 1 ? " call site, and " : " call sites, and ")
                + calls + (calls == 1 ? " total call was" : " total calls were")
                + " observed in representative base/PR runs.");

            JsonElement[] unexercised = coverage.GetProperty("files").EnumerateArray()
                .Where(file => !Bool(file, "exercised"))
                .ToArray();
            if (unexercised.Length > 0)
            {
                builder.AppendLine();
                builder.AppendLine("Not exercised (no behavioral claim): "
                    + string.Join(", ", unexercised.Select(file => "`" + NullableString(file, "filePath") + "`")) + ".");
                builder.AppendLine("Zero observed calls are not evidence that these files did not change behavior.");
            }
        }

        private static void AppendMembers(StringBuilder builder, JsonElement findings, string attribution, string heading)
        {
            if (!findings.TryGetProperty("members", out JsonElement members))
            {
                return;
            }

            JsonElement[] selected = members.EnumerateArray()
                .Where(member => String(member, "attribution") == attribution)
                .ToArray();
            if (selected.Length == 0)
            {
                return;
            }

            builder.AppendLine("### " + heading);
            builder.AppendLine("| Member | Call sites | Source | Evidence |");
            builder.AppendLine("|---|---:|---|---|");
            foreach (JsonElement member in selected)
            {
                string source = NullableString(member, "filePath") ?? "unresolved";
                int? line = NullableInt(member, "line");
                if (line is not null)
                {
                    source += ":" + line;
                }

                builder.AppendLine("| `" + Escape(String(member, "memberName")) + "` | "
                    + Int(member, "callSiteCount") + " | `" + Escape(source) + "` | "
                    + Escape(string.Join(", ", Strings(member, "symptoms").Take(3))) + " |");
            }
        }

        private static string RenderMember(JsonElement member, string marker)
        {
            var builder = new StringBuilder();
            builder.AppendLine("### Unexpected runtime behavior change");
            builder.AppendLine();
            builder.AppendLine("`" + String(member, "memberName") + "`");
            builder.AppendLine();
            builder.AppendLine("This member is in a file the PR did **not** modify, but its runtime behavior changed.");
            builder.AppendLine();
            builder.AppendLine("- Call sites: " + Int(member, "callSiteCount"));
            builder.AppendLine("- Distinct tests: " + Int(member, "distinctTestCount"));
            builder.AppendLine("- Verified frontier: " + Bool(member, "verified").ToString().ToLowerInvariant());
            foreach (string reason in Strings(member, "downgradeReasons").Take(2))
            {
                builder.AppendLine("- Downgrade: " + reason);
            }

            if (member.TryGetProperty("evidence", out JsonElement evidence))
            {
                builder.AppendLine();
                builder.AppendLine("Evidence (up to 5 observations):");
                foreach (JsonElement observation in evidence.EnumerateArray().Take(5))
                {
                    builder.AppendLine("- `" + String(observation, "testId") + "`: "
                        + RenderValue(NullableString(observation, "baseReturn"), NullableString(observation, "baseException"))
                        + " -> " + RenderValue(NullableString(observation, "prReturn"), NullableString(observation, "prException")));
                }
            }

            builder.AppendLine();
            builder.Append(marker);
            return builder.ToString();
        }

        private static string RenderValue(string? value, string? exception) =>
            exception is not null ? "exception `" + exception + "`" : "`" + (value ?? "(not rendered)") + "`";

        private static string MemberKey(string memberName)
        {
            byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(memberName));
            return Convert.ToHexString(hash).Substring(0, 16).ToLowerInvariant();
        }

        private static string RequiredEnvironment(string name)
        {
            string? value = Environment.GetEnvironmentVariable(name);
            if (string.IsNullOrWhiteSpace(value))
            {
                throw new CliException("GitHub posting requires " + name + ".");
            }

            return value;
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
            element.TryGetProperty(property, out JsonElement value) && value.ValueKind == JsonValueKind.Number
                ? value.GetInt32()
                : 0;

        private static int? NullableInt(JsonElement element, string property) =>
            element.TryGetProperty(property, out JsonElement value) && value.ValueKind == JsonValueKind.Number
                ? value.GetInt32()
                : null;

        private static bool Bool(JsonElement element, string property) =>
            element.TryGetProperty(property, out JsonElement value) && value.ValueKind == JsonValueKind.True;

        private static IEnumerable<string> Strings(JsonElement element, string property) =>
            element.TryGetProperty(property, out JsonElement values) && values.ValueKind == JsonValueKind.Array
                ? values.EnumerateArray().Select(value => value.GetString() ?? string.Empty)
                : Enumerable.Empty<string>();

        private static string Escape(string text) => text.Replace("|", "\\|", StringComparison.Ordinal).Replace("\r", " ").Replace("\n", " ");

        private static string Truncate(string text) => text.Length <= 1000 ? text : text.Substring(0, 1000);

        private sealed record ExistingComment(long Id, string Body);

        private sealed record GitHubContext(int PullRequestNumber, string Repository, string HeadSha, bool IsFork);

        private sealed class GitHubApiException : Exception
        {
            internal GitHubApiException(HttpStatusCode statusCode, string response)
                : base("GitHub REST " + (int)statusCode + " " + statusCode + ": " + response)
            {
                StatusCode = statusCode;
                Response = response;
            }

            internal HttpStatusCode StatusCode { get; }

            internal string Response { get; }
        }
    }
}
