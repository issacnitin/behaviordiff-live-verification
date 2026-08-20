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
        private const int MaxCommentLength = 60000;
        private const int MemberEvidenceBudget = 56000;
        private readonly JsonSerializerOptions _json = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
        private readonly HttpMessageHandler? _gitHubHandler;
        private readonly Func<string, AnthropicExplainer> _explainerFactory;

        internal GitHubPoster(
            HttpMessageHandler? gitHubHandler = null,
            Func<string, AnthropicExplainer>? explainerFactory = null)
        {
            _gitHubHandler = gitHubHandler;
            _explainerFactory = explainerFactory ?? (apiKey => new AnthropicExplainer(apiKey));
        }

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

            using var client = _gitHubHandler is null ? new HttpClient() : new HttpClient(_gitHubHandler, disposeHandler: false);
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
            IReadOnlyDictionary<string, ModelExplanation> explanations =
                new Dictionary<string, ModelExplanation>(StringComparer.Ordinal);

            var anchorFailures = new List<string>();
            string summaryMarker = "<!-- behaviordiff:github:pr:" + context.PullRequestNumber + ":summary -->";
            long summaryCommentId = await UpsertIssueComment(
                client,
                root,
                context.PullRequestNumber,
                issueComments,
                summaryMarker,
                RenderSummary(findings, summaryMarker, anchorFailures, explanations)).ConfigureAwait(false);
            if (!issueComments.Any(comment => comment.Body.Contains(summaryMarker, StringComparison.Ordinal)))
            {
                issueComments.Add(new ExistingComment(summaryCommentId, summaryMarker));
            }

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
                            RenderMember(
                                member,
                                marker,
                                explanations.TryGetValue(String(member, "memberName"), out ModelExplanation? explanation)
                                    ? explanation
                                    : null),
                            filePath,
                            line.Value).ConfigureAwait(false);
                    }
                    catch (GitHubApiException ex)
                    {
                        // GitHub review comments can only target a line in the unified PR diff. An
                        // UNEXPECTED file is normally absent from that diff by definition. Preserve the
                        // platform refusal in the summary rather than suppressing the finding or the post.
                        anchorFailures.Add(filePath + ":" + line.Value);
                        Console.WriteLine("  GitHub line comment not created: " + filePath + ":" + line.Value
                            + " - GitHub REST " + (int)ex.StatusCode + " " + ex.StatusCode + ": " + ex.Response);
                    }
                }
            }

            if (anchorFailures.Count > 0)
            {
                await UpsertIssueComment(
                    client,
                    root,
                    context.PullRequestNumber,
                    issueComments,
                    summaryMarker,
                    RenderSummary(findings, summaryMarker, anchorFailures, explanations)).ConfigureAwait(false);
            }

            try
            {
                explanations = await ExplainUnexpectedMembers(
                    client,
                    root,
                    context,
                    findings).ConfigureAwait(false);
                if (explanations.Count > 0)
                {
                    await UpsertIssueComment(
                        client,
                        root,
                        context.PullRequestNumber,
                        issueComments,
                        summaryMarker,
                        RenderSummary(findings, summaryMarker, anchorFailures, explanations)).ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("  Anthropic enrichment skipped after deterministic comment was posted: " + ex.Message);
            }

        }

        private async Task<List<ExistingComment>> ListComments(
            HttpClient client,
            string url,
            string bodyProperty)
        {
            var comments = new List<ExistingComment>();
            for (int page = 1; ; page++)
            {
                using JsonDocument response = await Send(
                    client,
                    HttpMethod.Get,
                    url + "&page=" + page,
                    body: null).ConfigureAwait(false);
                if (response.RootElement.ValueKind != JsonValueKind.Array)
                {
                    return comments;
                }

                JsonElement[] pageComments = response.RootElement.EnumerateArray().ToArray();
                foreach (JsonElement comment in pageComments)
                {
                    comments.Add(new ExistingComment(
                        comment.GetProperty("id").GetInt64(),
                        String(comment, bodyProperty)));
                }

                if (pageComments.Length < 100)
                {
                    return comments;
                }
            }
        }

        private async Task<IReadOnlyDictionary<string, ModelExplanation>> ExplainUnexpectedMembers(
            HttpClient gitHubClient,
            string root,
            GitHubContext context,
            JsonElement findings)
        {
            string? apiKey = Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY");
            if (string.IsNullOrWhiteSpace(apiKey)
                || String(findings, "status") != "analyzed"
                || !findings.TryGetProperty("members", out JsonElement members))
            {
                return new Dictionary<string, ModelExplanation>(StringComparer.Ordinal);
            }

            IReadOnlyList<ChangedFilePatch> patches = await ListChangedFilePatches(
                gitHubClient,
                root,
                context.PullRequestNumber,
                findings).ConfigureAwait(false);
            var explanations = new Dictionary<string, ModelExplanation>(StringComparer.Ordinal);
            using AnthropicExplainer explainer = _explainerFactory(apiKey);
            foreach (JsonElement member in members.EnumerateArray()
                .Where(item => String(item, "attribution") == "unexpected"))
            {
                string memberName = String(member, "memberName");
                try
                {
                    ModelExplanation? explanation = await explainer.ExplainAsync(member, patches).ConfigureAwait(false);
                    if (explanation is not null)
                    {
                        explanations[memberName] = explanation;
                    }
                    else
                    {
                        Console.WriteLine("  Anthropic explanation rejected by grounding validation: " + memberName);
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine("  Anthropic explanation unavailable for " + memberName + ": " + ex.Message);
                }
            }

            return explanations;
        }

        private async Task<IReadOnlyList<ChangedFilePatch>> ListChangedFilePatches(
            HttpClient client,
            string root,
            int pullRequestNumber,
            JsonElement findings)
        {
            var changedFiles = new HashSet<string>(
                findings.GetProperty("coverage").GetProperty("files").EnumerateArray()
                    .Select(file => NullableString(file, "filePath"))
                    .Where(path => path is not null)
                    .Select(path => path!),
                StringComparer.Ordinal);
            var patches = new List<ChangedFilePatch>();
            for (int page = 1; ; page++)
            {
                using JsonDocument response = await Send(
                    client,
                    HttpMethod.Get,
                    root + "/pulls/" + pullRequestNumber + "/files?per_page=100&page=" + page,
                    body: null).ConfigureAwait(false);
                JsonElement[] files = response.RootElement.ValueKind == JsonValueKind.Array
                    ? response.RootElement.EnumerateArray().ToArray()
                    : Array.Empty<JsonElement>();
                foreach (JsonElement file in files)
                {
                    string path = String(file, "filename");
                    if (changedFiles.Contains(path))
                    {
                        patches.Add(new ChangedFilePatch(
                            path,
                            NullableString(file, "patch") ?? "diff hunk unavailable"));
                    }
                }

                if (files.Length < 100)
                {
                    break;
                }
            }

            return patches;
        }

        private async Task<long> UpsertIssueComment(
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
                return match.Id;
            }

            using JsonDocument created = await Send(
                client,
                HttpMethod.Post,
                root + "/issues/" + pullRequestNumber + "/comments",
                new { body }).ConfigureAwait(false);
            long createdId = created.RootElement.GetProperty("id").GetInt64();
            Console.WriteLine("  created GitHub PR summary comment " + createdId);
            return createdId;
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

        internal static string RenderSummary(
            JsonElement findings,
            string marker,
            IReadOnlyList<string> anchorFailures,
            IReadOnlyDictionary<string, ModelExplanation>? explanations = null)
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
                string invalid = builder.ToString();
                return invalid.Length <= MaxCommentLength
                    ? invalid
                    : RenderInvalidBudgetFallback(findings, marker, reason);
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
                AppendMembers(builder, findings, "unexpected", "Unexpected members", explanations);
            }

            builder.AppendLine();
            builder.AppendLine("**EXPECTED: " + Int(summary, "expectedMembers") + " member(s), across "
                + Int(summary, "expectedCallSites") + " call site(s).**");
            AppendMembers(builder, findings, "expected", "Expected members", explanations);

            if (anchorFailures.Count > 0)
            {
                builder.AppendLine();
                builder.AppendLine("GitHub cannot anchor review comments on outside-diff files, which is why this analysis exists.");
            }

            builder.AppendLine();
            builder.Append(marker);
            string rendered = builder.ToString();
            return rendered.Length <= MaxCommentLength
                ? rendered
                : RenderBudgetFallback(findings, marker);
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
                    + string.Join(", ", unexercised.Take(20).Select(file => "`" + NullableString(file, "filePath") + "`"))
                    + (unexercised.Length > 20 ? ", and " + (unexercised.Length - 20) + " more in findings.json." : "."));
                builder.AppendLine("Zero observed calls are not evidence that these files did not change behavior.");
            }
        }

        private static void AppendMembers(
            StringBuilder builder,
            JsonElement findings,
            string attribution,
            string heading,
            IReadOnlyDictionary<string, ModelExplanation>? explanations)
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
            foreach (JsonElement member in selected)
            {
                int start = builder.Length;
                AppendMemberEvidence(
                    builder,
                    member,
                    explanations is not null
                        && explanations.TryGetValue(String(member, "memberName"), out ModelExplanation? explanation)
                            ? explanation
                            : null);
                if (builder.Length > MemberEvidenceBudget)
                {
                    builder.Length = start;
                    builder.AppendLine();
                    builder.AppendLine("- " + Code(String(member, "memberName"))
                        + ": full deterministic evidence would exceed GitHub's comment limit; inspect findings.json in the workflow artifact.");
                }
            }
        }

        private static string RenderBudgetFallback(JsonElement findings, string marker)
        {
            JsonElement summary = findings.GetProperty("summary");
            bool clean = String(findings, "verdict") == "clean";
            var builder = new StringBuilder();
            builder.AppendLine("## BehaviorDiff runtime analysis");
            builder.AppendLine();
            builder.AppendLine(clean
                ? "**No unexpected behavior changes were found in the observed execution.**"
                : "**UNEXPECTED: " + Int(summary, "unexpectedMembers") + " member(s), across "
                    + Int(summary, "unexpectedCallSites") + " call site(s).**");
            builder.AppendLine();
            builder.AppendLine("The complete deterministic evidence exceeded GitHub's comment limit. Inspect findings.json "
                + "in the workflow artifact. " + (clean
                    ? "The verdict remains clean only for the observed execution and reported coverage."
                    : "This is not a clean result."));
            if (findings.TryGetProperty("members", out JsonElement members))
            {
                foreach (JsonElement member in members.EnumerateArray()
                    .Where(item => String(item, "attribution") == "unexpected")
                    .Take(100))
                {
                    string line = "- " + Code(String(member, "memberName")) + " at " + Code(Source(member))
                        + ": " + Escape(String(member, "assertionReactionSummary"));
                    int remaining = MaxCommentLength - builder.Length - marker.Length - (Environment.NewLine.Length * 2);
                    if (remaining <= 0)
                    {
                        break;
                    }

                    builder.AppendLine(Truncate(line, remaining));
                }
            }

            builder.AppendLine();
            builder.Append(marker);
            return builder.ToString();
        }

        private static string RenderInvalidBudgetFallback(
            JsonElement findings,
            string marker,
            string reason)
        {
            var builder = new StringBuilder();
            builder.AppendLine("## BehaviorDiff: analysis could not complete");
            builder.AppendLine();
            builder.AppendLine("**No safety verdict was produced.** This is not a clean result.");
            builder.AppendLine();
            builder.AppendLine("> " + Truncate(reason, 2000).Replace("\n", "\n> "));
            builder.AppendLine();
            builder.AppendLine("The full refusal reason exceeded GitHub's comment limit; inspect findings.json in the workflow artifact.");
            builder.AppendLine();
            builder.Append(marker);
            return builder.ToString();
        }

        private static void AppendMemberEvidence(
            StringBuilder builder,
            JsonElement member,
            ModelExplanation? explanation)
        {
            string source = Source(member);
            builder.AppendLine();
            builder.AppendLine("<details>");
            builder.AppendLine("<summary><code>" + WebUtility.HtmlEncode(String(member, "memberName")) + "</code> - "
                + Int(member, "distinctTestCount") + (Int(member, "distinctTestCount") == 1 ? " test, " : " tests, ")
                + Int(member, "callSiteCount") + (Int(member, "callSiteCount") == 1 ? " call site" : " call sites")
                + "</summary>");
            builder.AppendLine();
            builder.AppendLine("**Observed values**");
            if (member.TryGetProperty("evidence", out JsonElement evidence))
            {
                foreach (JsonElement observation in evidence.EnumerateArray().Take(3))
                {
                    builder.AppendLine("- " + RenderObservation(member, observation));
                }
            }

            if (member.TryGetProperty("consequences", out JsonElement consequences)
                && consequences.ValueKind == JsonValueKind.Array
                && consequences.GetArrayLength() > 0)
            {
                builder.AppendLine();
                builder.AppendLine("**Downstream consequences**");
                foreach (JsonElement consequence in consequences.EnumerateArray().Take(3))
                {
                    builder.AppendLine("- " + RenderConsequence(consequence));
                }
            }

            builder.AppendLine();
            builder.AppendLine("**Tests and assertions**");
            builder.AppendLine("- **" + Escape(String(member, "assertionReactionSummary")) + "**");
            if (member.TryGetProperty("evidence", out evidence))
            {
                foreach (JsonElement observation in evidence.EnumerateArray()
                    .GroupBy(item => String(item, "testId"), StringComparer.Ordinal)
                    .Select(group => group.First()))
                {
                    string reaction = NullableBool(observation, "assertionReacted") switch
                    {
                        true => "an assertion reacted",
                        false => "no assertion reacted",
                        null => "assertion reaction was not available",
                    };
                    builder.AppendLine("- " + Code(String(observation, "testId")) + ": " + reaction + ".");
                }
            }

            builder.AppendLine();
            builder.AppendLine("**Call paths**");
            if (member.TryGetProperty("evidence", out evidence))
            {
                foreach (JsonElement observation in evidence.EnumerateArray().Take(3))
                {
                    AppendCallPaths(builder, observation);
                }
            }

            builder.AppendLine();
            builder.AppendLine("**Source**");
            builder.AppendLine("- " + Code(source));
            builder.AppendLine();
            builder.AppendLine("**Edited-file reachability**");
            string[] changedFiles = Strings(member, "changedFilesReachingMember").ToArray();
            builder.AppendLine(changedFiles.Length == 0
                ? "- No edited file appears on these recorded test-to-member paths."
                : "- " + string.Join(", ", changedFiles.Select(Code)));
            if (explanation is not null)
            {
                builder.AppendLine();
                builder.AppendLine("**Optional model explanation** (" + Code(explanation.Model)
                    + ", accepted only after literal and exact-citation grounding checks)");
                builder.AppendLine("- Why: " + explanation.Why);
                builder.AppendLine("- Suggested test: " + explanation.Test);
            }

            builder.AppendLine();
            builder.AppendLine("</details>");
        }

        private static string RenderObservation(JsonElement member, JsonElement observation)
        {
            if (NullableInt(observation, "ordinal") is not int ordinal || ordinal < 0)
            {
                return Code(String(observation, "testId")) + ": " + Code(String(observation, "kind"))
                    + " - " + Escape(String(observation, "detail"))
                    + ". No single call occurrence can be aligned, so concrete values and paths are unavailable.";
            }

            string baseInvocation = Invocation(
                String(member, "memberName"),
                NullableString(observation, "baseArgs"));
            string prInvocation = Invocation(
                String(member, "memberName"),
                NullableString(observation, "prArgs"));
            string baseResult = RenderValue(
                NullableString(observation, "baseReturn"),
                NullableString(observation, "baseException"));
            string prResult = RenderValue(
                NullableString(observation, "prReturn"),
                NullableString(observation, "prException"));
            string invocation = string.Equals(baseInvocation, prInvocation, StringComparison.Ordinal)
                ? Code(baseInvocation)
                : "base " + Code(baseInvocation) + ", PR " + Code(prInvocation);
            return Code(String(observation, "testId")) + ": " + invocation + " returned "
                + baseResult + "; PR returns " + prResult + ".";
        }

        private static string RenderConsequence(JsonElement consequence)
        {
            JsonElement observation = consequence.GetProperty("evidence");
            string memberName = String(consequence, "memberName");
            string baseResult = RenderValue(
                NullableString(observation, "baseReturn"),
                NullableString(observation, "baseException"));
            string prResult = RenderValue(
                NullableString(observation, "prReturn"),
                NullableString(observation, "prException"));
            return Code(String(observation, "testId")) + ": " + Code(memberName)
                + " returned " + baseResult + "; PR returns " + prResult + ".";
        }

        private static void AppendCallPaths(StringBuilder builder, JsonElement observation)
        {
            string test = String(observation, "testId");
            string basePath = RenderPath(observation, "baseCallPath");
            string prPath = RenderPath(observation, "prCallPath");
            if (string.Equals(basePath, prPath, StringComparison.Ordinal))
            {
                builder.AppendLine("- " + Code(test) + " (base and PR): " + basePath);
                return;
            }

            builder.AppendLine("- " + Code(test) + " (base): " + basePath);
            builder.AppendLine("- " + Code(test) + " (PR): " + prPath);
        }

        private static string RenderPath(JsonElement observation, string property)
        {
            if (!observation.TryGetProperty(property, out JsonElement path)
                || path.ValueKind != JsonValueKind.Array
                || path.GetArrayLength() == 0)
            {
                return "path unavailable";
            }

            return string.Join(" -> ", path.EnumerateArray().Select(node => Code(String(node, "memberName"))));
        }

        private static string Invocation(string memberName, string? args)
        {
            int paren = memberName.IndexOf('(');
            string withoutParameters = paren < 0 ? memberName : memberName.Substring(0, paren);
            int dot = withoutParameters.LastIndexOf('.');
            string method = dot < 0 ? withoutParameters : withoutParameters.Substring(dot + 1);
            return method + "(" + (args ?? "arguments not rendered") + ")";
        }

        private static string Source(JsonElement member)
        {
            string source = NullableString(member, "filePath") ?? "unresolved";
            int? line = NullableInt(member, "line");
            return line is null ? source : source + ":" + line;
        }

        private static string RenderMember(
            JsonElement member,
            string marker,
            ModelExplanation? explanation)
        {
            var builder = new StringBuilder();
            builder.AppendLine("### Unexpected runtime behavior change");
            builder.AppendLine();
            builder.AppendLine("`" + String(member, "memberName") + "`");
            builder.AppendLine();
            builder.AppendLine("This member is in a file the PR did **not** modify, but its runtime behavior changed.");
            builder.AppendLine();
            AppendMemberEvidence(builder, member, explanation);
            foreach (string reason in Strings(member, "downgradeReasons").Take(2))
            {
                builder.AppendLine("- Downgrade: " + reason);
            }

            builder.AppendLine();
            builder.Append(marker);
            return builder.ToString();
        }

        private static string RenderValue(string? value, string? exception) =>
            exception is not null ? "exception " + Code(exception) : Code(value ?? "(not rendered)");

        private static string Code(string text) => "`" + text.Replace("`", "'", StringComparison.Ordinal) + "`";

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

        private static bool? NullableBool(JsonElement element, string property) =>
            element.TryGetProperty(property, out JsonElement value)
                ? value.ValueKind switch
                {
                    JsonValueKind.True => true,
                    JsonValueKind.False => false,
                    _ => null,
                }
                : null;

        private static IEnumerable<string> Strings(JsonElement element, string property) =>
            element.TryGetProperty(property, out JsonElement values) && values.ValueKind == JsonValueKind.Array
                ? values.EnumerateArray().Select(value => value.GetString() ?? string.Empty)
                : Enumerable.Empty<string>();

        private static string Escape(string text) => text.Replace("|", "\\|", StringComparison.Ordinal).Replace("\r", " ").Replace("\n", " ");

        private static string Truncate(string text) => Truncate(text, 1000);

        private static string Truncate(string text, int length) => text.Length <= length ? text : text.Substring(0, length);

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
