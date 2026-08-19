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
   excluded as noise. What remains is a divergence.
4. **Frontier.** Divergences are placed in the call tree, and the *shallowest* diverging call in each
   subtree is the finding — its diverging descendants are collateral, not separate findings. Each
   finding is attributed EXPECTED (in a file the PR edited) or UNEXPECTED (not).

## The demo

**Part one — SampleApp, the config parser.** A one-line change to a default constant in
`SettingsParser.cs`. The finding surfaces in `ShippingCalculator.cs`, a file the change never touched,
with six diverging keys collapsing to two findings.

```powershell
pwsh -File tools/verify-diff.ps1 -Mutate -Change config
```

```text
collapse                 : 6 diverged keys -> 2 frontier node(s)  (3.0x)
EXPECTED (edited file)   : 0 member(s), across 0 call site(s)
UNEXPECTED (headline)    : 1 member(s), across 2 call site(s)
  file : samples/SampleApp/ShippingCalculator.cs
```

**Part two — FluentValidation, a real merged PR.** Upstream PR #2136 removes sync-over-async across
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

The GitHub path was verified on public PR
[`issacnitin/behaviordiff-live-verification#1`](https://github.com/issacnitin/behaviordiff-live-verification/pull/1).
The hosted run took 30.63 seconds inside BehaviorDiff, peaked at 217,276 KB RSS, and wrote 24,557,295
trace bytes. GitHub accepted the idempotent summary comment. It rejected the attempted line comment
with HTTP 422 because the unexpected file is absent from the PR diff; the summary records that
platform limitation and the exact file/line instead of pretending the thread was posted.

## Limitations

These are deliberate and measured, not unknowns.

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
pwsh -File tools/verify-mcp.ps1              # MCP reads canonical findings.json
pwsh -File tools/verify-ado-post.ps1         # local ADO REST contract and idempotency
pwsh -File tools/verify-pipeline.ps1         # mocked end-to-end CI seams
```
