# Backend equivalence: Harmony vs Cecil (sync methods)

Both runs: `DOTNET_JITMinOpts=1`, same test suite, 49/49 passing, same run directory
contents apart from the woven assemblies. SampleApp and SampleApp.Tests both woven.

## Four guards

| Guard | Requirement | Result |
|---|---|---|
| a | matched keys > 100 | **219** |
| b | identical method sets, Harmony-only excess == declined set | Harmony-only **10**, all 10 declined; Cecil-only **0** |
| c | identical per-key event counts | **0** keys differ |
| d | identical per-key ordinal sequences | **0** keys differ |

Ordinal sequence = the ordered list of `argsDigest\|returnDigest\|exceptionType` per
`(testId, methodFullName)`, so this checks emission order, not just digest membership.

Supporting counts, all asserted non-zero before use:

```
events           harmony 7642   cecil 7619
  on common methods       7619         7619   (exactly equal)
methods          harmony  111   cecil  101
declined parsed from manifest        10
key sets         harmony-only 0   cecil-only 0
```

The 23-event, 10-method difference is entirely the async methods the weaver declines
(`WeaverAsyncNotSupported`): 2 in SampleApp, 8 in SampleApp.Tests. Guard b asserts the
excess equals that set exactly and fails on anything else, so this is accounted for
rather than tolerated.

## Resolution tripwire (regression guard, reported separately)

Not a live risk — both backends call the same `SourceLocationResolver` on the same
`MethodBase`. Present so a future Cecil-native reimplementation cannot pass silently:
FilePath, Line and FilePathResolution are outside the join key, so a divergence there
would clear all four guards and surface only as wrong attribution.

```
methods compared: 101
methods differing FilePath/Line/Resolution: 0
resolution kinds: sequencePoints 90, declaringType 11
```

The 11 `declaringType` results are implicit constructors with no sequence points, which
report line 0 rather than borrowing a sibling's — reproduced identically by both backends
because the logic is shared, not reimplemented.

## Scope

Sync methods only. Async remains Harmony-only and is the next increment. This result does
not cover async continuation ordering.
