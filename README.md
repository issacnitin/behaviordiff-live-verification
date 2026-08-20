# BehaviorDiff

Diffs the **runtime behavior** of two builds of a C# repository, not its source text.

A source diff tells you which files changed. It cannot tell you what those changes *did*, and in
particular it cannot tell you when a change to one file altered behavior somewhere else. BehaviorDiff
runs the repository's own test suite against both builds, records what every instrumented method was
called with and what it returned, and reports the differences — attributed to the file that caused them
where possible, and honestly marked where not.

The finding that matters is the one in a file the pull request never touched.

## How it works

1. **Weave.** A Mono.Cecil pass rewrites the assemblies at build time, wrapping each in-scope method so
   it reports its arguments, return value, and exceptions. Nothing is patched at runtime.
2. **Run.** The repository's test suite runs three times on the base build and once on the PR build.
   The three base runs establish which behavior is nondeterministic, so that nondeterminism is not
   charged to the PR.
3. **Diff.** Events are matched on `(test, member)`. Differences that also appear between base runs are
   excluded as noise. What remains is a divergence. Every edited file is also reported with its
   executed members, call sites, and base/PR call counts; zero is retained as "not observed," not clean.
4. **Frontier.** Divergences are placed in the call tree, and the *shallowest* diverging call in each
   subtree is the finding — its diverging descendants are collateral, not separate findings. Each
   finding is attributed EXPECTED (in a file the PR edited) or UNEXPECTED (not).

## The demo

**Primary — price-cache key regression.** The only edited file, `CacheSettings.cs`, removes customer tier
from the price-cache key to improve hit rate. Its property and type initializer are deliberately skipped by
the tracer, so it contributes zero traced members or calls. In unedited code, a Gold lookup for product 123
warms `P:123` with the discounted price 80, and a later Standard lookup hits that same entry instead of
computing the full price 100. The Gold test stays green, the positive-price test stays green precisely because
the wrong price is still positive, and the Standard full-price test catches the regression.

```powershell
pwsh -File tools/verify-diff.ps1 -Mutate -Change cache
```

The deterministic run reports 14 diverged keys collapsed to nine frontier call sites. The headline is
the unedited `PriceCache.BuildKey`: all three tests execute it, while two have no assertion react.

**Secondary — payment retry regression.** `payment-config.json` omits `max_attempts`, so the base
`ConfigParser.cs` resolves the inherited value `10`. The one edited line replaces that lookup with a
local-key check and a default of `3`. The parser's traced `(args, void return)` behavior stays identical,
but the unedited `RetryPolicy.ShouldRetry(503, 5)` changes from `true` to `false`. A payment gateway that
recovers on attempt 2 still passes, leaving an untested call site under a green test; one that recovers on
attempt 7 now fails because the unedited `PaymentClient` abandons the transient outage at attempt 3.

```powershell
pwsh -File tools/verify-diff.ps1 -Mutate -Change retry
```

```text
collapse                 : 5 diverged keys -> 2 frontier node(s)  (2.5x)
EXPECTED (edited file)   : 0 member(s), across 0 call site(s)
UNEXPECTED (headline)    : 1 member(s), across 2 call site(s)
   frontier  SampleApp.RetryPolicy.ShouldRetry(System.Int32,System.Int32)
   untested: True
```

**Third mode — config parser.** The original `SettingsParser.cs` threshold mutation remains available;
its six diverged keys collapse to two call-site frontiers for one unexpected member.

```powershell
pwsh -File tools/verify-diff.ps1 -Mutate -Change config
```

All three demo modes enforce the same constraints in one proof: exactly one edited file has no
divergence/frontier footprint, collapse is above `1x`, and the headline has an `untested: True`
observation. The cache mode additionally proves that the edited file has zero traced members and calls.

```powershell
pwsh -File tools/verify-demo-fixtures.ps1
```

To run the production Anthropic explainer for one mode, protect the key with Windows DPAPI once, then run:

```powershell
New-Item "$HOME/.behaviordiff" -ItemType Directory -Force | Out-Null
Read-Host -AsSecureString 'Anthropic API key' |
   ConvertFrom-SecureString |
   Set-Content "$HOME/.behaviordiff/anthropic.key"
pwsh -File tools/run-real-explainer.ps1 -Change cache
```

The command prints the raw Messages API response, the unchanged literal/citation validation verdict,
and the final rendered comment. The protected key can only be decrypted by the same Windows user on the
same machine; it is not exported while target code builds or tests. A validation rejection exits nonzero
and is reported without weakening the checks.

**Scale case — FluentValidation, a real merged PR.** Upstream PR #2136 removes sync-over-async across
`AbstractValidator` and the internal rule-execution path. Demonstrates the tool at scale on a library
nobody here wrote.

```powershell
pwsh -File tools/fluentvalidation-pipeline.ps1
```

```text
EXPECTED (edited file)   : 11 member(s), across 2962 call site(s)
UNEXPECTED (headline)    : 11 member(s), across  147 call site(s)
source-generated members : 4
end to end               : 51.9s
```

## Running it on your own repository

```powershell
# 1. Build the target, then weave the assemblies you want observed.
pwsh -File tools/Stage-WovenSample.ps1 -TreeRoot <repo> -OutDir <staging>

# 2. Run the suite once per base run and once for the PR, with a distinct trace path each time.
$env:BEHAVIORDIFF_BACKEND    = 'cecil'
$env:BEHAVIORDIFF_NAMESPACES = '<YourRootNamespace>'
$env:BEHAVIORDIFF_TRACE      = '<run-dir>/run.ndjson'
dotnet test <staging>/YourTests.dll

# 3. Diff, then attribute.
dotnet run --project src/BehaviorDiff.Engine -- diff `
    --base1 <b1> --base2 <b2> --base3 <b3> --pr <pr> `
    --changed-files <git-diff-name-only.txt> `
    --base-root <base-tree> --pr-root <pr-tree> --out divergence-set.json

dotnet run --project src/BehaviorDiff.Engine -- frontier `
    --in divergence-set.json --changed-files <git-diff-name-only.txt> --out frontier.json
```

Exit codes: `0` clean, `1` findings, `3` run invalid (refused), `4`/`5` build failure.

The engine **refuses** rather than reporting a clean result it cannot justify — for example when no
changed file contributed a single traced member, or when path normalization failed. A refusal is exit
3 and names the reason.

## Pull request pipeline

**Azure DevOps is the leading review experience for the outside-diff demo.** A live disposable PR
confirmed that `threadContext` accepts an unchanged file and shows the thread on the PR Overview page
by default. Reviewers do not need to open the file manually. The `AccessControl.cs:8` thread remained
active after a second source iteration, was returned when comparing iterations 1 and 2, and repeated
BehaviorDiff posts updated the same thread/comment IDs instead of creating duplicates.

The live proof used
[`OnlineServices/BehaviorDiff-Thread-20260820-2128 PR 772049`](https://devdiv.visualstudio.com/OnlineServices/_git/BehaviorDiff-Thread-20260820-2128/pullrequest/772049):
the PR changes only `AccountStatus.cs`; summary thread `9954025` has no file context; finding thread
`9954026` targets the unedited `/samples/SampleApp/AccessControl.cs` at line 8. A dedicated throwaway
project could not be created because the account lacks the organization-level `Create new projects`
permission, so the test used a uniquely named disposable repository in the existing `OnlineServices`
project and did not touch any existing repository.

`azure-pipelines.yml` runs the same CLI used locally; there is no build-task extension. It resolves
Azure DevOps PR refs with `--ci=azuredevops`, writes `findings.json`, posts the result, publishes that
artifact, and deletes the large trace work directory after every job.

`.github/workflows/blastradius.yml` provides the equivalent GitHub Actions path. It resolves the
immutable `github.event.pull_request.base.sha` and `head.sha` values from `GITHUB_EVENT_PATH`, not
branch names. Its `actions/checkout` step sets `fetch-depth: 0`; the default depth of 1 cannot create
both worktrees or calculate their merge base, and `--ci=github` refuses shallow clones explicitly.

Azure Repos PR validation is configured through a **Build validation branch policy**, not a YAML
`pr` trigger. Attach this pipeline to each target branch that should be checked; that policy is the PR
trigger. The YAML uses `trigger: none` so source-branch pushes do not duplicate the merge-commit run.

```text
behaviordiff <repo> --ci=azuredevops --findings findings.json
behaviordiff post --provider=azuredevops --findings findings.json
behaviordiff <repo> --ci=github --findings findings.json
behaviordiff post --provider=github --findings findings.json
```

The posting gate defaults to `warn-only`. Set the pipeline variable `behaviorDiffGate` to
`fail-on-findings` only after the signal is trusted. A refusal posts a prominent non-verdict and stays
nonblocking; it is never converted into an empty finding list.

GitHub comments lead with the downstream consequence, then retain collapsed deterministic observations,
deduplicated call-path shapes, and assertion reaction under a details disclosure. The summary deep-links
the unedited source at the immutable PR head. Because GitHub cannot anchor a review thread outside the PR
diff, BehaviorDiff instead anchors a cause comment on the first added line in the changed hunk; when
several lines may participate, the comment says it is a hunk-level anchor rather than claiming one line.

Checks API annotations are not a substitute. A live experiment successfully attached an annotation to
an unchanged file, but reviewers saw no annotation on PR Overview or Files changed. The annotation text
appeared only after expanding and selecting the check on the Checks tab, and its commit link did not
render the annotation because the file was outside that commit diff. BehaviorDiff therefore does not use
Checks annotations for outside-diff findings. The neutral experiment remains visible in
[`run 96344946687`](https://github.com/issacnitin/behaviordiff-live-verification/runs/96344946687).

Model explanation is optional. A trusted posting process may set `ANTHROPIC_API_KEY` to request one
Anthropic Messages API explanation per unexpected member; without that variable, no model request is
made. Do not expose a persistent model credential to a `pull_request` job that builds code from the PR;
the included workflow deliberately does not do so. The request includes only the member evidence and
the PR's changed-file diff hunks. BehaviorDiff posts the response below the deterministic evidence only
when it contains the observed values, member name, and a changed identifier selected from the supplied
hunk, and both claims carry exact citations copied from the supplied observation/diff corpus. An
unavailable API or rejected response does not change the deterministic finding. See both complete config-parser renderings in
[`evidence/CONFIG-PARSER-COMMENTS.md`](evidence/CONFIG-PARSER-COMMENTS.md).

The GitHub path was verified on public PR
[`issacnitin/behaviordiff-live-verification#1`](https://github.com/issacnitin/behaviordiff-live-verification/pull/1).
The final coverage-aware hosted run took 37.14 seconds inside BehaviorDiff, peaked at 224,264 KB RSS,
and wrote 24,543,695 trace bytes. It reported 1 of 1 edited files exercised (1 member, 5 call sites,
10 calls). GitHub accepted the idempotent summary comment. It rejected the attempted line comment
with HTTP 422 because the unexpected file is absent from the PR diff; the summary records that
platform limitation and the exact file/line instead of pretending the thread was posted.

## Limitations

These are deliberate and measured, not unknowns.

**BehaviorDiff analyzes executed code only.** A method that no selected test calls has no runtime
history to compare. Every changed file therefore carries execution coverage: traced members, distinct
`(test, member)` call sites, and raw calls in one representative base run plus the PR run. A zero row
means "not observed" and supports no claim about unchanged behavior. BehaviorDiff complements static
analysis; it does not replace it.

**Source-generated members are unreachable by path attribution.** A method emitted by a source
generator lives under `obj/`, and no git diff will ever name that file. FluentValidation #2136 adds four
such members, including one with 822 calls. The tool reports them as `source-generated, not in the git
diff` rather than letting them look like a miss — but it cannot attribute them to the PR.

**Type initializers are not instrumented.** A static constructor runs under the CLR's
type-initialization lock. A hook called from inside one can load a type whose own initializer is blocked
behind a lock the caller holds. This project hit that deadlock once already, from a static constructor
in a test adapter, and its failure mode is a startup hang with no output. Build-time weaving does not
make it safe — the woven call still executes under that lock.

**Property and event accessors, and operators, are skipped.** A scope decision, not a technical limit.
It keeps the trace focused on methods that do work, at the cost of `DescendantSkipped` downgrades when
a finding's subtree calls one.

**The noise baseline is a sample, not a characterisation.** Three base runs on FluentValidation
identified 2,326 nondeterministic keys; a fourth run raised that to 2,388. Three runs therefore catch
roughly 97% of what four would, and the residue is charged to the PR as divergences. The engine prints
this in its own output rather than leaving it implicit. More base runs shrink the residue and cost one
suite run each.

**Memory is about 3.2x trace size.** 416 MB of traces peaked at 1.33 GB. Traces stream in, but every
parsed event is retained for the comparison, so peak scales with event count. FluentValidation at four
runs produces 555 MB.

**Generic types are instrumented; that was not always true.** The Harmony backend could not patch open
generic definitions. Cecil rewrites the definition and every instantiation runs the woven body. Most of
the .NET ecosystem is generic, so this was the difference between working on a sample and working on a
library.

## Repository layout

| Path | Contents |
| --- | --- |
| `src/BehaviorDiff.Contracts` | Wire formats: trace events, coverage manifest |
| `src/BehaviorDiff.Tracer` | Runtime hooks, digesting, scope rules (`MethodSelector`) |
| `src/BehaviorDiff.Engine` | `diff` and `frontier` commands |
| `src/BehaviorDiff.Cli` | Repository scan and orchestration |
| `tools/Weaver` | The Cecil weaver |
| `tools/verify-*.ps1` | Executable proofs — each asserts and exits non-zero on failure |
| `evidence/FINDINGS.md` | Measured results behind the claims above |

## Proofs

Every claim in this README has a script that re-derives it.

```powershell
pwsh -File tools/verify-contracts.ps1        # wire format round-trips
pwsh -File tools/verify-call-order.ps1       # call ordering stable under parallelism
pwsh -File tools/verify-correlation.ps1      # test attribution matches the framework's own
pwsh -File tools/verify-negative-tests.ps1   # a no-PDB assembly invalidates the run
pwsh -File tools/verify-null-task.ps1        # the null-Task path emits exactly once
pwsh -File tools/verify-diff.ps1 -Mutate -Change config
pwsh -File tools/verify-ci-refs.ps1          # merge-base refs and invalid findings arms
pwsh -File tools/verify-github-refs.ps1      # event SHAs and shallow-clone refusal
pwsh -File tools/verify-coverage.ps1         # values, paths, assertion reaction, and coverage honesty
pwsh -File tools/verify-anthropic.ps1        # one request/member and grounded-output rejection
pwsh -File tools/verify-demo-fixtures.ps1    # cache, retry, config constraints
pwsh -File tools/verify-mcp.ps1              # MCP reads canonical findings.json
pwsh -File tools/verify-ado-post.ps1         # local ADO REST contract and idempotency
pwsh -File tools/verify-pipeline.ps1         # mocked end-to-end CI seams
```
