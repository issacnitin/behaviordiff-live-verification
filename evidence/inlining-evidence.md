# Inlining evidence: Harmony−MinOpts vs Cecil−MinOpts

Data point 2 of the four-run matrix (Harmony±MinOpts, Cecil±MinOpts). Not a failed
equivalence attempt — the two runs differ in instrumentation backend AND in scope
(only SampleApp.dll was woven; Harmony also covers SampleApp.Tests), so the method
sets are not expected to match. The comparable subset is the methods present in both.

Produced accidentally: `DOTNET_JITMinOpts=1` was omitted, which both `tools/trace-sample.ps1`
and `Cli/Program.cs` normally set. Same test suite, 49/49 passing on both runs.

## Result

| | Harmony−MinOpts | Cecil−MinOpts |
|---|---:|---:|
| events | 1,480 | 7,511 |
| distinct methods | 82 | 46 |

**29 methods produced zero events under Harmony and non-zero under Cecil,
totalling 6,157 lost events. All 29 are reported `Patched` in Harmony's manifest.**

Methods that match exactly (`DeepNode.Build` 1278/1278, `Catalog..ctor` 15/15) are
recursive or too large to inline. The split is by inlinability, not by backend defect.

## Why it matters

`PatchInstaller` warns about precisely this:

> "The JIT will inline small methods, whose calls then bypass the patch and produce no
> events even though this manifest reports them as Patched. Inlining decisions can differ
> between the two builds being compared, so this shows up as a false behavior difference."

The manifest asserts coverage the tracer did not have. Nothing downstream can detect it:
the engine reads `Patched` and treats silence as "no calls occurred" rather than
"not observed". That is this tool's own central failure mode occurring inside its own
instrumentation layer, and it is invisible without a second backend to compare against.

Weaving removes the failure structurally rather than by configuration: the hooks are
inside the method body, so an inlined callee carries its instrumentation with it.

## Open question

Whether Cecil needs `JITMinOpts` at all is **not** established by this run. The test is
Cecil+MinOpts vs Cecil−MinOpts: identical per-method counts would confirm it. Any method
that differs means weaving did not fully solve inlining.

Raw per-method counts: `inlining-evidence.csv` (111 rows).
