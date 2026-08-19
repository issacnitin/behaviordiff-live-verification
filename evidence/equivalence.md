# Backend equivalence: Harmony vs Cecil (sync + async)

Supersedes the sync-only result. Both runs `DOTNET_JITMinOpts=1`, same suite, 49/49
passing, SampleApp and SampleApp.Tests both woven.

## Four guards — no exceptions

| Guard | Requirement | Result |
|---|---|---|
| a | matched keys > 100 | **242** |
| b | identical method sets | Harmony-only **0**, Cecil-only **0** |
| c | identical per-key event counts | **0** differ |
| d | identical per-key ordinal sequences | **0** differ |

```
events   harmony 7642   cecil 7642    (exactly equal)
methods  harmony  111   cecil  111
keys     harmony-only 0   cecil-only 0
declined by the weaver: 0
```

Guard b is now unconditional. The previous run needed an accounting clause for 10
declined async methods; that set is empty.

Weave coverage: SampleApp 87 = 55 woven + 32 skipped; SampleApp.Tests 93 = 64 woven +
29 skipped. Both reconcile.

## Async

Emission mirrors `TracePatches` rather than being designed fresh:

- The kickoff method is woven, not the state machine type — `MethodSelector.EvaluateType`
  already returns `StateMachineType` for the latter, so both backends skip it identically.
- At the synchronous return the hook calls `AttachContinuation` and then `EndCall(null)`,
  in that order. `AttachContinuation` sets `DeferredToContinuation`; `EndCall` restores the
  parent frame and suppresses its own emit because that flag is set. The event is produced
  later by the continuation, on `TaskScheduler.Default`.
- The frame survives into the continuation because `AttachContinuation` captures it in the
  closure — method scope is not relied on.
- All four `ReturnKind`s handled. `Task`/`Task<T>` pass the value through; `ValueTask`/
  `ValueTask<T>` are converted with `AsTask()` and the hook **returns the replacement**,
  which the epilogue stores back, because a ValueTask backed by an `IValueTaskSource` may
  only be consumed once.
- `Task<T>`/`ValueTask<T>` hooks are generic and the weaver binds `T` from the method's
  return type, so the result renderer is the typed `((Task<T>)completed).Result` Harmony
  uses rather than a reflective lookup.

Throw-before-first-await: an `async` method captures the exception into a faulted Task
rather than throwing synchronously, so Harmony's finalizer sees `__exception == null` and
the continuation reports the exception. The weaver takes the same path. `QuoteAsync`'s
invalid-quantity test exercises it, and both backends emit 8 events for that method.

```
   7  SampleApp.InventoryClient.IsInStockAsync(String)      identical both backends
   8  SampleApp.OrderService.QuoteAsync(String, Int32)      identical both backends
```

## Resolution tripwire (regression guard)

```
methods compared: 111
methods differing FilePath/Line/Resolution: 0
```

## Known inherited quirk

`AttachContinuation` emits immediately when handed a null Task without setting
`DeferredToContinuation`, after which `EndCall` emits again — a double event. Reachable
only from a non-async method declared to return `Task` that returns null. The weaver
reproduces it deliberately: the goal here is equivalence, not correcting Harmony. Worth
fixing in the tracer, where it fixes both backends at once.
