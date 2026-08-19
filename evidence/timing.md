# Timing: Harmony vs Cecil

Same suite, 49 tests, 7642 events on both backends (equivalence verified separately).
Three reps per configuration, minimum reported to reduce noise. Wall clock of
`dotnet test` on a pre-built directory.

## Run time

| Configuration | min | median | over baseline |
|---|---:|---:|---:|
| baseline, no tracing | 1069 ms | 1080 ms | — |
| Harmony +MinOpts | 1594 ms | 1633 ms | **+525 ms (+49%)** |
| Cecil +MinOpts | 1099 ms | 1116 ms | **+30 ms (+2.8%)** |
| Cecil −MinOpts | 1137 ms | 1183 ms | +68 ms (+6.4%) |

Cecil −MinOpts being marginally slower than +MinOpts is not a contradiction: `JITMinOpts`
reduces JIT *compilation* work, which on a ~1 s run can outweigh the slower code it
produces. The two are within noise of each other; the meaningful comparison is either
against baseline.

## One-off cost, reported separately

| | cost | paid |
|---|---:|---|
| Harmony patch install | **697 ms** | every run |
| Cecil weave, both assemblies | 1731 ms | once, at build |

Harmony's figure is from its own manifest: the patch phase ends 697 ms after tracer start,
with SampleApp.Tests alone taking a 307 ms window. That is the bulk of the +525 ms measured
overhead, and it recurs on every single run.

The Cecil figure is an **upper bound** and mostly not weaving: it includes two `dotnet run`
host startups (~700–800 ms each). Actual rewrite work is the remainder. It is not measured
precisely here because it does not need to be — it is paid once per build, not per run.

## Per-call cost — not separable at this scale

With 7642 events, one-off cost dominates and per-call cost cannot be isolated honestly.
Dividing gives Harmony ~69 µs/event and Cecil ~4 µs/event, but both figures are mostly
install cost divided by event count, not per-call cost. **Do not quote them.**

What can be said: Harmony's overhead on this workload is dominated by patch installation,
which is why it scales with *method count* rather than *call count*. Cecil moves that cost
to build time entirely. Separating per-call cost needs a workload with far more calls per
method — FluentValidation is the next opportunity.

## Trace size

```
harmony  5,115,474 bytes
cecil    5,116,679 bytes     (+1,205, 0.02%)
```

~670 bytes per event. Backends produce near-identical volume, as expected given identical
event counts and digests.

## Reading

For a demo, the relevant number is that Cecil adds **2.8%** to a run that Harmony adds
**49%** to, and that Harmony's cost is per-run while Cecil's is per-build. On a large suite
the gap widens: patch install scales with the number of instrumented methods, and every
`dotnet test` invocation pays it again.
