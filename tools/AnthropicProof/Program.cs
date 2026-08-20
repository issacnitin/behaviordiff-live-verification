using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using BehaviorDiff.Cli;

if (args.Length != 1 || !File.Exists(args[0]))
{
    Console.Error.WriteLine("usage: BehaviorDiff.AnthropicProof <findings.json>");
    return 2;
}

using JsonDocument findings = JsonDocument.Parse(File.ReadAllText(args[0]));
JsonElement member = findings.RootElement.GetProperty("members").EnumerateArray()
    .First(item => item.GetProperty("attribution").GetString() == "unexpected");
var patches = new[]
{
    new ChangedFilePatch(
        "samples/SampleApp/SettingsParser.cs",
        "@@ -10,7 +10,7 @@ namespace SampleApp\n-private const decimal DefaultFreeShippingThreshold = 50m;\n+private const decimal DefaultFreeShippingThreshold = 30m;"),
};

const string observationCitation = "OBSERVATION: test=SampleApp.Tests.ShippingTests.Order_below_threshold_pays_shipping; baseArgs=orderTotal=Primitive:40; prArgs=orderTotal=Primitive:40; baseReturn=Primitive:false; prReturn=Primitive:true; baseException=; prException=";
const string diffCitation = "DIFF: samples/SampleApp/SettingsParser.cs: +private const decimal DefaultFreeShippingThreshold = 30m;";
var whyCitations = new List<string> { observationCitation, diffCitation };
if (member.TryGetProperty("consequences", out JsonElement consequences)
    && consequences.ValueKind == JsonValueKind.Array
    && consequences.GetArrayLength() > 0)
{
    JsonElement consequence = consequences[0];
    JsonElement consequenceEvidence = consequence.GetProperty("evidence");
    whyCitations.Add("CONSEQUENCE: test=" + consequenceEvidence.GetProperty("testId").GetString()
        + "; member=" + consequence.GetProperty("memberName").GetString()
        + "; baseReturn=" + consequenceEvidence.GetProperty("baseReturn").GetString()
        + "; prReturn=" + consequenceEvidence.GetProperty("prReturn").GetString()
        + "; baseException=" + (consequenceEvidence.TryGetProperty("baseException", out JsonElement baseException)
            ? baseException.GetString()
            : string.Empty)
        + "; prException=" + (consequenceEvidence.TryGetProperty("prException", out JsonElement prException)
            ? prException.GetString()
            : string.Empty));
}

string acceptedText = JsonSerializer.Serialize(new
{
    why = new
    {
        text = "The PR changes DefaultFreeShippingThreshold, and IsFreeShipping now returns true instead of false for the observed input 40.",
        citations = whyCitations,
    },
    test = new
    {
        text = "Add a test that applies the default settings, calls IsFreeShipping with 40, and expects false; it passes on base and fails on the PR because the result is true.",
        citations = new[] { observationCitation },
    },
});
var acceptedHandler = new FakeHandler(Response(acceptedText));
ModelExplanation? accepted;
using (var explainer = new AnthropicExplainer(
    "proof-key",
    acceptedHandler,
    "https://example.invalid/v1/messages",
    "claude-sonnet-5"))
{
    accepted = await explainer.ExplainAsync(member, patches);
}

Assert(accepted is not null, "grounded response was rejected");
Assert(acceptedHandler.RequestCount == 1, "expected exactly one request for one member");
Assert(acceptedHandler.LastRequestBody.Contains("DefaultFreeShippingThreshold", StringComparison.Ordinal), "diff identifier missing from prompt");
Assert(acceptedHandler.LastRequestBody.Contains("baseCallPath", StringComparison.Ordinal), "call path missing from prompt");
Assert(acceptedHandler.LastRequestBody.Contains("assertionReaction", StringComparison.Ordinal), "untested evidence missing from prompt");
Assert(acceptedHandler.LastRequestBody.Contains("CONSEQUENCE:", StringComparison.Ordinal), "downstream consequence missing from prompt");
Assert(acceptedHandler.SawApiKey && acceptedHandler.SawVersion, "required Anthropic headers missing");

string rejectedText = JsonSerializer.Serialize(new
{
    why = new
    {
        text = "The behavior changed for an unknown reason.",
        citations = new[]
        {
            "OBSERVATION: test=SampleApp.Tests.ShippingTests.Order_below_threshold_pays_shipping; baseArgs=orderTotal=Primitive:40; prArgs=orderTotal=Primitive:40; baseReturn=Primitive:false; prReturn=Primitive:true; baseException=; prException=",
            "DIFF: samples/SampleApp/SettingsParser.cs: +private const decimal DefaultFreeShippingThreshold = 30m;",
        },
    },
    test = new
    {
        text = "Add a regression test.",
        citations = new[]
        {
            "OBSERVATION: test=SampleApp.Tests.ShippingTests.Order_below_threshold_pays_shipping; baseArgs=orderTotal=Primitive:40; prArgs=orderTotal=Primitive:40; baseReturn=Primitive:false; prReturn=Primitive:true; baseException=; prException=",
        },
    },
});
var rejectedHandler = new FakeHandler(Response(rejectedText));
using (var explainer = new AnthropicExplainer(
    "proof-key",
    rejectedHandler,
    "https://example.invalid/v1/messages",
    "claude-sonnet-5"))
{
    ExplanationAttempt rejected = await explainer.ExplainWithDiagnosticsAsync(member, patches);
    Assert(rejected.Explanation is null, "ungrounded response was accepted");
}

string fabricatedCitationText = JsonSerializer.Serialize(new
{
    why = new
    {
        text = "DefaultFreeShippingThreshold makes IsFreeShipping change from false to true for 40.",
        citations = new[]
        {
            "OBSERVATION: fabricated",
            "DIFF: fabricated",
        },
    },
    test = new
    {
        text = "Test IsFreeShipping with 40 and expect false rather than true after DefaultFreeShippingThreshold.",
        citations = new[] { "OBSERVATION: fabricated" },
    },
});
var fabricatedHandler = new FakeHandler(Response(fabricatedCitationText));
using (var explainer = new AnthropicExplainer(
    "proof-key",
    fabricatedHandler,
    "https://example.invalid/v1/messages",
    "claude-sonnet-5"))
{
    ExplanationAttempt rejected = await explainer.ExplainWithDiagnosticsAsync(member, patches);
    Assert(rejected.Explanation is null, "fabricated evidence citations were accepted");
}

string wrongCaseText = acceptedText.Replace("IsFreeShipping", "isFreeShipping", StringComparison.Ordinal);
var wrongCaseHandler = new FakeHandler(Response(wrongCaseText));
using (var explainer = new AnthropicExplainer(
    "proof-key",
    wrongCaseHandler,
    "https://example.invalid/v1/messages",
    "claude-sonnet-5"))
{
    ExplanationAttempt rejected = await explainer.ExplainWithDiagnosticsAsync(member, patches);
    Assert(rejected.Explanation is null, "wrong-case grounding literal was accepted");
}

const string errorBody = "{\"type\":\"error\",\"error\":{\"message\":\"proof failure\"}}";
var errorHandler = new FakeHandler(errorBody, statusCode: HttpStatusCode.BadRequest);
using (var explainer = new AnthropicExplainer(
    "proof-key",
    errorHandler,
    "https://example.invalid/v1/messages",
    "claude-sonnet-5"))
{
    ExplanationAttempt attempt = await explainer.ExplainWithDiagnosticsAsync(member, patches);
    Assert(attempt.RawResponse == errorBody, "non-success raw response was lost");
    Assert(attempt.Explanation is null && attempt.Validation.Contains("400", StringComparison.Ordinal),
        "non-success response was not reported as rejected");
}

const string malformedBody = "not-json";
var malformedHandler = new FakeHandler(malformedBody);
using (var explainer = new AnthropicExplainer(
    "proof-key",
    malformedHandler,
    "https://example.invalid/v1/messages",
    "claude-sonnet-5"))
{
    ExplanationAttempt attempt = await explainer.ExplainWithDiagnosticsAsync(member, patches);
    Assert(attempt.RawResponse == malformedBody, "malformed raw response was lost");
    Assert(attempt.Explanation is null && attempt.Validation.Contains("malformed", StringComparison.Ordinal),
        "malformed response was not reported as rejected");
}

string marker = "<!-- proof -->";
string withoutKey = GitHubPoster.RenderSummary(
    findings.RootElement,
    marker,
    new[] { "samples/SampleApp/ShippingCalculator.cs:10" });
var explanations = new Dictionary<string, ModelExplanation>(StringComparer.Ordinal)
{
    [member.GetProperty("memberName").GetString() ?? string.Empty] = accepted!,
};
string withKey = GitHubPoster.RenderSummary(
    findings.RootElement,
    marker,
    new[] { "samples/SampleApp/ShippingCalculator.cs:10" },
    explanations);
Assert(!withoutKey.Contains("Optional model explanation", StringComparison.Ordinal), "no-key comment contains model output");
Assert(withKey.Contains("Optional model explanation", StringComparison.Ordinal), "key-enabled comment omitted accepted model output");
Assert(withKey.Contains("accepted only after literal and exact-citation grounding checks", StringComparison.Ordinal), "model output is not labeled as validated");
Assert(!withKey.Contains("422", StringComparison.Ordinal), "comment leaked raw GitHub 422 details");

JsonObject oversized = JsonNode.Parse(File.ReadAllText(args[0]))?.AsObject()
    ?? throw new InvalidOperationException("could not create oversized findings proof");
JsonArray oversizedEvidence = oversized["members"]?[0]?["evidence"]?.AsArray()
    ?? throw new InvalidOperationException("oversized proof has no evidence");
foreach (JsonNode? observation in oversizedEvidence.Take(3))
{
    observation!["baseArgs"] = new string('x', 30000);
    observation["prArgs"] = new string('y', 30000);
}
using JsonDocument oversizedDocument = JsonDocument.Parse(oversized.ToJsonString());
string budgeted = GitHubPoster.RenderSummary(
    oversizedDocument.RootElement,
    marker,
    new[] { "samples/SampleApp/ShippingCalculator.cs:10" });
Assert(budgeted.Length <= 60000, "deterministic comment exceeded GitHub body budget");
Assert(budgeted.Contains("workflow artifact", StringComparison.Ordinal), "oversized comment omitted artifact fallback");

JsonObject cleanOversized = JsonNode.Parse(oversized.ToJsonString())?.AsObject()
    ?? throw new InvalidOperationException("could not create clean budget proof");
cleanOversized["verdict"] = "clean";
cleanOversized["summary"]!["unexpectedMembers"] = 0;
cleanOversized["summary"]!["unexpectedCallSites"] = 0;
cleanOversized["members"] = new JsonArray();
using JsonDocument cleanOversizedDocument = JsonDocument.Parse(cleanOversized.ToJsonString());
string cleanBudgeted = GitHubPoster.RenderSummary(
    cleanOversizedDocument.RootElement,
    marker,
    Array.Empty<string>());
Assert(cleanBudgeted.Contains("No unexpected behavior changes", StringComparison.Ordinal), "oversized clean result lost its clean verdict");
Assert(!cleanBudgeted.Contains("not a clean result", StringComparison.OrdinalIgnoreCase), "oversized clean result was mislabeled non-clean");

using JsonDocument refusedDocument = JsonDocument.Parse(JsonSerializer.Serialize(new
{
    schema = "behaviordiff.findings/1",
    status = "refused",
    verdict = "could_not_analyze",
    refusal = new { reason = new string('r', 70000) },
}));
string refusedBudgeted = GitHubPoster.RenderSummary(
    refusedDocument.RootElement,
    marker,
    Array.Empty<string>());
Assert(refusedBudgeted.Length <= 60000, "refused comment exceeded GitHub body budget");
Assert(refusedBudgeted.Contains("No safety verdict was produced", StringComparison.Ordinal), "refused fallback lost non-verdict warning");

JsonObject unalignable = JsonNode.Parse(File.ReadAllText(args[0]))?.AsObject()
    ?? throw new InvalidOperationException("could not create negative ordinal proof");
JsonObject unalignableObservation = unalignable["members"]?[0]?["evidence"]?[0]?.AsObject()
    ?? throw new InvalidOperationException("negative ordinal proof has no evidence");
unalignableObservation["ordinal"] = -1;
unalignableObservation["kind"] = "CallCountChange";
unalignableObservation["detail"] = "called 2 time(s) in base, 3 in PR";
using JsonDocument unalignableDocument = JsonDocument.Parse(unalignable.ToJsonString());
string unalignableComment = GitHubPoster.RenderSummary(
    unalignableDocument.RootElement,
    marker,
    Array.Empty<string>());
Assert(unalignableComment.Contains("CallCountChange", StringComparison.Ordinal), "unalignable divergence kind was omitted");
Assert(unalignableComment.Contains("concrete values and paths are unavailable", StringComparison.Ordinal), "unalignable divergence did not explain evidence limits");
Assert(!unalignableComment.Contains("IsFreeShipping(orderTotal=Primitive:40)", StringComparison.Ordinal), "negative ordinal rendered arbitrary first-call values");

JsonObject missingOrdinal = JsonNode.Parse(unalignable.ToJsonString())?.AsObject()
    ?? throw new InvalidOperationException("could not create missing ordinal proof");
missingOrdinal["members"]?[0]?["evidence"]?[0]?.AsObject().Remove("ordinal");
using JsonDocument missingOrdinalDocument = JsonDocument.Parse(missingOrdinal.ToJsonString());
string missingOrdinalComment = GitHubPoster.RenderSummary(
    missingOrdinalDocument.RootElement,
    marker,
    Array.Empty<string>());
Assert(missingOrdinalComment.Contains("concrete values and paths are unavailable", StringComparison.Ordinal), "missing ordinal did not use unalignable rendering");
Assert(!missingOrdinalComment.Contains("IsFreeShipping(orderTotal=Primitive:40)", StringComparison.Ordinal), "missing ordinal rendered arbitrary first-call values");

var requestOrder = new List<string>();
var gitHubHandler = new GitHubHandler(requestOrder, patches[0]);
var postingModelHandler = new FakeHandler(Response(acceptedText), () => requestOrder.Add("anthropic:post"));
string eventPath = Path.GetTempFileName();
File.WriteAllText(eventPath, JsonSerializer.Serialize(new
{
    number = 1,
    pull_request = new
    {
        head = new { sha = "proof-head", repo = new { full_name = "example/repo", fork = false } },
        @base = new { repo = new { full_name = "example/repo", fork = false } },
    },
}));
string? originalEvent = Environment.GetEnvironmentVariable("GITHUB_EVENT_PATH");
string? originalRepository = Environment.GetEnvironmentVariable("GITHUB_REPOSITORY");
string? originalToken = Environment.GetEnvironmentVariable("GITHUB_TOKEN");
string? originalApi = Environment.GetEnvironmentVariable("GITHUB_API_URL");
string? originalAnthropic = Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY");
try
{
    Environment.SetEnvironmentVariable("GITHUB_EVENT_PATH", eventPath);
    Environment.SetEnvironmentVariable("GITHUB_REPOSITORY", "example/repo");
    Environment.SetEnvironmentVariable("GITHUB_TOKEN", "proof-github-token");
    Environment.SetEnvironmentVariable("GITHUB_API_URL", "https://github.example/api/v3");
    Environment.SetEnvironmentVariable("ANTHROPIC_API_KEY", "proof-anthropic-key");
    var poster = new GitHubPoster(
        gitHubHandler,
        apiKey => new AnthropicExplainer(
            apiKey,
            postingModelHandler,
            "https://anthropic.example/v1/messages",
            "claude-sonnet-5"));
    await poster.PostAsync(findings.RootElement);

    int deterministicPost = requestOrder.IndexOf("github:post-summary");
    int reviewAttempt = requestOrder.IndexOf("github:post-review");
    int anchorPatch = requestOrder.IndexOf("github:patch-anchor");
    int diffFetch = requestOrder.IndexOf("github:get-files");
    int modelPost = requestOrder.IndexOf("anthropic:post");
    int enrichedPatch = requestOrder.IndexOf("github:patch-enriched");
    Assert(deterministicPost >= 0 && deterministicPost < reviewAttempt && reviewAttempt < anchorPatch
        && anchorPatch < diffFetch && diffFetch < modelPost && modelPost < enrichedPatch,
        "optional enrichment ran before deterministic summary posting");
    Assert(gitHubHandler.SummaryPostCount == 1, "enrichment created a duplicate summary comment");
    Assert(gitHubHandler.SummaryPatchCount == 2, "anchor/model enrichment did not update the deterministic summary comment");

    Environment.SetEnvironmentVariable("ANTHROPIC_API_KEY", null);
    var noKeyHandler = new GitHubHandler(new List<string>(), patches[0]);
    bool noKeyModelCalled = false;
    var noKeyPoster = new GitHubPoster(
        noKeyHandler,
        apiKey =>
        {
            noKeyModelCalled = true;
            return new AnthropicExplainer(apiKey);
        });
    await noKeyPoster.PostAsync(findings.RootElement);
    Assert(!noKeyModelCalled, "no-key posting invoked the model client");
}
finally
{
    Environment.SetEnvironmentVariable("GITHUB_EVENT_PATH", originalEvent);
    Environment.SetEnvironmentVariable("GITHUB_REPOSITORY", originalRepository);
    Environment.SetEnvironmentVariable("GITHUB_TOKEN", originalToken);
    Environment.SetEnvironmentVariable("GITHUB_API_URL", originalApi);
    Environment.SetEnvironmentVariable("ANTHROPIC_API_KEY", originalAnthropic);
    File.Delete(eventPath);
}

Console.WriteLine("=== WITHOUT ANTHROPIC_API_KEY ===");
Console.WriteLine(withoutKey);
Console.WriteLine();
Console.WriteLine("=== WITH ANTHROPIC_API_KEY (FAKE GROUNDED RESPONSE) ===");
Console.WriteLine(withKey);
Console.WriteLine();
Console.WriteLine("PASS: one request per unexpected member");
Console.WriteLine("PASS: prompt contains observations, paths, assertion reaction, and diff hunk");
Console.WriteLine("PASS: missing/wrong-case literals and fabricated citations are rejected");
Console.WriteLine("PASS: raw non-success and malformed responses survive diagnostic validation");
Console.WriteLine("PASS: deterministic post precedes enrichment and the same comment is patched");
Console.WriteLine("PASS: no-key posting never invokes Anthropic");
Console.WriteLine("PASS: oversized evidence falls back below GitHub's body limit");
Console.WriteLine("PASS: refused and clean budget fallbacks preserve their verdicts");
Console.WriteLine("PASS: negative ordinals render kind/detail without arbitrary values");
Console.WriteLine("PASS: missing ordinals are treated as unalignable");
Console.WriteLine("verify-anthropic: PASS");
return 0;

static string Response(string text) => JsonSerializer.Serialize(new
{
    content = new[] { new { type = "text", text } },
});

static void Assert(bool condition, string message)
{
    if (!condition)
    {
        throw new InvalidOperationException(message);
    }
}

sealed class FakeHandler(
    string responseBody,
    Action? onRequest = null,
    HttpStatusCode statusCode = HttpStatusCode.OK) : HttpMessageHandler
{
    internal int RequestCount { get; private set; }

    internal string LastRequestBody { get; private set; } = string.Empty;

    internal bool SawApiKey { get; private set; }

    internal bool SawVersion { get; private set; }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        onRequest?.Invoke();
        RequestCount++;
        LastRequestBody = request.Content is null
            ? string.Empty
            : await request.Content.ReadAsStringAsync(cancellationToken);
        SawApiKey = request.Headers.Contains("X-Api-Key");
        SawVersion = request.Headers.Contains("anthropic-version");
        return new HttpResponseMessage(statusCode)
        {
            Content = new StringContent(responseBody, Encoding.UTF8, "application/json"),
        };
    }
}

sealed class GitHubHandler(List<string> order, ChangedFilePatch patch) : HttpMessageHandler
{
    internal int SummaryPostCount { get; private set; }

    internal int SummaryPatchCount { get; private set; }

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        string path = request.RequestUri?.AbsolutePath ?? string.Empty;
        string body;
        HttpStatusCode status = HttpStatusCode.OK;
        if (request.Method == HttpMethod.Get && path.EndsWith("/issues/1/comments", StringComparison.Ordinal))
        {
            body = "[]";
        }
        else if (request.Method == HttpMethod.Get && path.EndsWith("/pulls/1/comments", StringComparison.Ordinal))
        {
            body = "[]";
        }
        else if (request.Method == HttpMethod.Get && path.EndsWith("/pulls/1/files", StringComparison.Ordinal))
        {
            order.Add("github:get-files");
            body = JsonSerializer.Serialize(new[] { new { filename = patch.FilePath, patch = patch.Patch } });
        }
        else if (request.Method == HttpMethod.Post && path.EndsWith("/pulls/1/comments", StringComparison.Ordinal))
        {
            order.Add("github:post-review");
            status = HttpStatusCode.UnprocessableEntity;
            body = "{\"message\":\"outside diff\"}";
        }
        else if (request.Method == HttpMethod.Post && path.EndsWith("/issues/1/comments", StringComparison.Ordinal))
        {
            order.Add("github:post-summary");
            SummaryPostCount++;
            body = "{\"id\":123}";
        }
        else if (request.Method == HttpMethod.Patch && path.EndsWith("/issues/comments/123", StringComparison.Ordinal))
        {
            SummaryPatchCount++;
            order.Add(SummaryPatchCount == 1 ? "github:patch-anchor" : "github:patch-enriched");
            body = "{\"id\":123}";
        }
        else
        {
            throw new InvalidOperationException("Unexpected fake GitHub request: " + request.Method + " " + request.RequestUri);
        }

        return Task.FromResult(new HttpResponseMessage(status)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        });
    }
}
