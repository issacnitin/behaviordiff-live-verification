# Findings

Measured results behind the claims in the README. Written for a reader who has not seen the build.

Every number here was produced by a script in `tools/`, on a Windows laptop, .NET SDK 8.0.424.

---

## 1. Runtime patching lost a third of the call graph to inlining

**Why this was measured.** The original instrumentation backend was Harmony, which rewrites methods at
runtime. A method the JIT has already inlined has no call to intercept, so it reports as instrumented
and then never fires. That is the tool's worst failure mode: a member the manifest calls `Patched`,
producing no events, indistinguishable from a member that was genuinely never called.

**Method.** Same test suite, same subject, both backends, both with `DOTNET_JITMinOpts=1` — the knob
that actually disables inlining in a retail runtime. (`DOTNET_JitNoInline` is the intuitive one and is
compiled out of retail builds; it does nothing. That was measured directly.)

| | events | methods with ≥1 event |
| --- | ---: | ---: |
| Harmony | 1,480 | 82 |
| Cecil | 7,511 | 46 |

**Result.** **29 methods scored zero events under Harmony and non-zero under Cecil, accounting for
6,157 events. All 29 reported `Patched` in the Harmony manifest.** Recursive and large methods, which
the JIT will not inline, matched exactly — `DeepNode.Build` 1278/1278, `Catalog..ctor` 15/15 —
confirming the difference is inlining and not a counting error.

Build-time weaving has no such gap: the IL is rewritten before the JIT ever sees it. Cecil with and
without `JITMinOpts` produced **7,619 = 7,619 events across 101 = 101 methods, 0 methods differing**.

Raw per-method data: `evidence/inlining-evidence.csv` (111 rows).

---

## 2. The two backends agreed exactly, before they were allowed to differ

**Why this was measured.** Replacing the instrumentation backend is only safe if the replacement sees
the same thing. Before Harmony was removed, the two were run against the same suite and compared on
four independent guards plus a tripwire.

**Result**, sync and async paths, Harmony+MinOpts against Cecil+MinOpts:

```text
events                     7,642 = 7,642
methods                      111 =   111
(a) matched keys                    242
(b) harmony-only / cecil-only     0 / 0
(c) key present, counts differ        0
(d) key present, digests differ       0
tripwire: 111 compared, 0 differing
```

Async per-method: `IsInStockAsync` 7/7, `QuoteAsync` 8/8.

**This equivalence was later broken on purpose.** Harmony cannot patch open generic definitions —
its prefix and postfix are closed methods that would have to be built per type argument. Cecil rewrites
the definition itself and every instantiation runs that body, so the restriction was Harmony's, not the
tool's. Lifting it took FluentValidation's instrumented surface from 204 to 638 members in the library
alone, and made the PR's own edited types visible for the first time. At that point Harmony stopped
being the reference and was removed.

---

## 3. Weaving costs 2.8%; patching cost 49%

**Why this was measured.** Instrumentation that changes the timing of the thing it observes is
self-defeating, and a per-run installation cost is paid on every analysis.

Three repetitions, minimum taken:

| Configuration | wall clock | overhead |
| --- | ---: | ---: |
| No tracing | 1,069 ms | — |
| Harmony + MinOpts | 1,594 ms | +525 ms (+49%) |
| Cecil + MinOpts | 1,099 ms | +30 ms (+2.8%) |
| Cecil, no MinOpts | 1,137 ms | +68 ms (+6.4%) |

One-off costs are reported separately because they are paid at different times:

- **Harmony patch install: 697 ms on every run.** Paid four times per analysis.
- **Cecil weave: 1,731 ms once, at build.** Upper bound — includes two `dotnet run` startups.

Trace output was within 0.02% between the two backends (5,115,474 vs 5,116,679 bytes), so the cost
difference is not explained by one of them recording less.

Per-call cost is deliberately **not** quoted at this scale: at 7,642 events the install cost dominates
and any per-call figure derived from it would be an artifact of the division.

---

## 4. FluentValidation #2136 — a real merged PR

**Subject.** Upstream commit `6eac0afe`, "Improve performance by removing sync-over-async by generating
sync methods using Zomp.SyncMethodGenerator". Chosen over four other candidates because it touches
`AbstractValidator` and the internal rule-execution path that every validation traverses, and changes
no test file — so every test key matches on both sides.

**Scale.**

```text
instrumented    : 638 members in the library, 1,054 in the test assembly
events per run  : 105,743
traces          : 555 MB across four runs
matched keys    : 45,519
```

**Findings.**

```text
EXPECTED (edited file)   : 11 member(s), across 2962 call site(s)
UNEXPECTED (headline)    : 11 member(s), across  147 call site(s)
source-generated members : 4
```

The 11 EXPECTED members are the sync-over-async removal itself, seen as members appearing and
disappearing:

```text
Patched -> absent  AbstractValidator`1.ValidateInternalAsync(ctx, bool, CancellationToken)
absent -> Patched  AbstractValidator`1.ValidateInternalAsync(ctx, CancellationToken)
Patched -> absent  PropertyRule`2.ValidateAsync(ctx, bool, CancellationToken)
absent -> Patched  RuleComponent`2.Validate(ctx, TProperty)
...
```

**Stability.** Two full executions of the pipeline gave **identical** EXPECTED counts — 876
`MethodAdded` and 2,086 `MethodRemoved` both times — because a member appearing or disappearing is a
property of the binaries, not of scheduling. Only the UNEXPECTED residue moved (70 and 163), and it is
entirely in unedited files: a `ConcurrentDictionary` key comparer and a lazily-initialised localisation
cache, both known nondeterminism.

**What it could not attribute.** Four members added by the PR are emitted by a source generator into
`obj/`, including `AbstractValidator.ValidateInternal` with 822 calls. No git diff names those files.
The tool reports them explicitly rather than omitting them.

---

## 5. Cost and stability of the pipeline

```text
four test runs   30.7 s
engine diff      10.8 s      peak working set 1.33 GB against 416 MB of traces
engine frontier   2.7 s      peak working set 0.41 GB
weave + staging  ~7   s
------------------------------
end to end       51.9 s
```

**Noise baseline sampling.** Nondeterministic keys found, by number of base runs:

| base runs | keys | delta |
| ---: | ---: | ---: |
| 2 | 2,122 | — |
| 3 | 2,326 | +204 |
| 4 | 2,388 | +62 |

Three runs is the operating point: it captures ~97% of what four finds, and the curve is decelerating.
Moving from two runs to three took base-vs-PR divergences on FluentValidation from 520 and 761 across
two executions down to 57 and 54.

**Run-to-run variation of the subject itself.** Four runs of the *same woven bytes* differed by 14
events total, all of it in two methods — both overloads of `AccessorCache`1+Key.Equals`, called by a
`ConcurrentDictionary` during lookup. The method *set* was byte-identical across all four runs, which
is what rules out the instrumentation as the cause: if generic instantiation were driving it, which
methods emitted would vary, not just how often.

---

## 6. Azure Pipelines fit

Microsoft's current hosted-agent specification (checked 2026-08-19) is 2 CPU cores, 7 GB VM RAM,
with Linux jobs limited to 6 GB physical memory, and 10 GB of storage available for source and build
outputs. Sources:

- <https://learn.microsoft.com/azure/devops/pipelines/agents/hosted#hardware>
- <https://learn.microsoft.com/azure/devops/pipelines/agents/hosted#capabilities-and-limitations>

FluentValidation's measured local footprint compared with those limits:

| Resource | BehaviorDiff | Hosted limit | Share |
| --- | ---: | ---: | ---: |
| trace files | 555,099,175 bytes | 10 GB usable disk | 5.6% |
| complete BehaviorDiff work dir | 651,692,990 bytes | 10 GB usable disk | 6.5% |
| diff peak working set | 1.33 GB | 6 GB Linux physical-memory cgroup | 22% |
| wall clock | 51.9 s local | 60 min free private-project job | 1.4% of job duration |

The 651.7 MB work directory includes four trace directories (three base, one PR), staged binaries,
the divergence set, and the frontier report. The pipeline keeps `findings.json` as a small artifact
and removes the work directory under `condition: always()`, so self-hosted agents do not accumulate
trace data between pipeline invocations. Microsoft-hosted agents are fresh per job, but cleanup still
prevents later steps in the same job from competing with 555 MB of traces.

**Decision: keep four total test runs (three base + one PR) initially.** Disk and memory have ample
headroom, and the third base run is the one that reduced residual divergences from 520/761 to 57/54.
Dropping to three total runs would save one suite invocation at the cost of substantially weaker noise
characterisation. Revisit only after the hosted measurement says time is the bottleneck.

The generic CLI path was exercised against the external FluentValidation worktree at `ef50516d`,
using `HEAD` for both sides. It wove 3 project assemblies per side and produced all four non-empty
traces: 138,991,695; 138,926,750; 138,890,682; and 138,900,889 bytes. The frontier then refused
because 108 residual nodes survived while the same-commit changed-file set was empty. Its canonical
result was `status=refused`, `isCleanResult=false`, with no `members` array. That is the expected
behavior: the run proves orchestration and capacity, not that one sample can justify a clean verdict.

**Hosted wall clock is not yet measured.** This checkout has Azure DevOps access but no BehaviorDiff
PR or pipeline context; its inherited remote points at an unrelated repository, so pushing the tool
there merely to obtain a timing would contaminate another project. `azure-pipelines.yml` prints one
`HOSTED MEASUREMENT` line with wall time, parent-process peak working set, trace bytes, and analysis
exit code on the first valid PR run, alongside the 51.9 s local reference.

**Live REST posting is also unverified.** `tools/verify-ado-post.ps1` proves the documented API 7.1
routes and payloads against a local HTTP service: first run creates one summary plus one line-anchored
member thread; re-push updates both; refusal updates the summary with its exact reason; warn-only exits
0 and fail-on-findings exits 1. It cannot prove Azure Repos authorization or whether the service will
track a line comment in an UNEXPECTED file, because that file is absent from the PR diff and therefore
has no iteration `changeTrackingId` to supply.

---

## 7. Live GitHub verification

Public fixture: <https://github.com/issacnitin/behaviordiff-live-verification/pull/1>

The PR changed one line in `SettingsParser.cs` (`50m` to `30m`). Two final hosted runs completed
successfully and produced equivalent analyzed artifacts:

| Run | Job duration | BehaviorDiff wall | Peak RSS | Trace bytes |
| --- | ---: | ---: | ---: | ---: |
| [32266828793](https://github.com/issacnitin/behaviordiff-live-verification/actions/runs/32266828793) | 57 s | 33.10 s | 215,532 KB | 24,546,252 |
| [32266826312](https://github.com/issacnitin/behaviordiff-live-verification/actions/runs/32266826312) | 61 s | 30.63 s | 217,276 KB | 24,557,295 |

The final `findings.json` was 2,055 bytes and reported:

```text
status      : analyzed
verdict     : findings
UNEXPECTED  : 1 member, 2 call sites
member      : SampleApp.ShippingCalculator.IsFreeShipping(System.Decimal)
source      : samples/SampleApp/ShippingCalculator.cs:10
observed    : Primitive:false -> Primitive:true (two tests)
```

GitHub updated one issue comment idempotently. The Markdown table and headings rendered correctly.
There was one ugly but intentional detail: the line-comment limitation bullet contains GitHub's raw
422 JSON response. GitHub review comments can only target files in the unified PR diff, while this
finding is unexpected precisely because `ShippingCalculator.cs` was not edited. No review comment was
created; the summary includes the resolved source line and the exact rejection instead.
