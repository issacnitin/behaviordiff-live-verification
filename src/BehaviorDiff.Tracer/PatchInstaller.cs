using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Text;
using System.Threading;
using BehaviorDiff.Contracts;
using HarmonyLib;

namespace BehaviorDiff.Tracer
{
    /// <summary>
    /// Scans loaded assemblies, patches every eligible member in the configured namespaces, and records
    /// a coverage manifest describing everything it found and everything it could not observe.
    /// </summary>
    /// <remarks>
    /// <para><b>Trace runs require <c>DOTNET_JITMinOpts=1</c>.</b></para>
    /// <para>
    /// Harmony works by redirecting a method's native entry point, which only intercepts calls that go
    /// through that entry point. When the JIT inlines a callee into its caller, the callee's body is
    /// copied into the caller's native code and no call is made, so the detour is never reached and the
    /// method produces no events. Small methods are the ones the JIT inlines most eagerly, and small
    /// methods are common in the code being diffed.
    /// </para>
    /// <para>
    /// It is worse than uniform data loss. Inlining decisions depend on tiered compilation, call-site
    /// count, and IL size, all of which can differ between the two builds being compared, which turns a
    /// missing event into a phantom behavior difference.
    /// </para>
    /// <para>
    /// <c>DOTNET_JitNoInline=1</c> is the obvious knob and does not work: it is a checked-build JIT
    /// config and is compiled out of the retail runtime, so setting it silently does nothing. This was
    /// measured, not assumed - with it set, one-field-store constructors produced no events while Harmony
    /// still reported the patch as registered. <c>DOTNET_JITMinOpts=1</c> forces MinOpts, which is
    /// honoured in retail and disables inlining, and it recovers those calls.
    /// </para>
    /// <para>
    /// MinOpts disables every other optimisation too, so it perturbs timing far more than a targeted
    /// no-inline would. That is an acceptable trade for a trace run, but it does mean timing-sensitive
    /// behavior is not being observed under production codegen. The variable must be set before the
    /// process starts; the runtime reads it during startup.
    /// </para>
    /// </remarks>
    internal sealed class PatchInstaller
    {
        private const string JitMinOptsVariable = "DOTNET_JITMinOpts";

        private const BindingFlags MemberFlags =
            BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;

        private readonly TracerOptions _options;
        private readonly SourceLocationResolver _locations;
        private readonly Action<MethodBase, MethodTraceInfo> _register;
        private readonly Harmony _harmony = new Harmony("behaviordiff.tracer");
        private readonly Stopwatch _clock = Stopwatch.StartNew();

        // The AssemblyLoad callback only enqueues. Patching runs from a drain loop, never inside the
        // callback: patching resolves signature types, which loads more assemblies and re-enters the
        // callback, and doing that work while the loader is still finishing a load risks deadlock.
        private readonly ConcurrentQueue<Assembly> _pending = new ConcurrentQueue<Assembly>();
        private readonly ManualResetEventSlim _pendingSignal = new ManualResetEventSlim(false);
        private readonly object _drainGate = new object();

        private readonly HashSet<Assembly> _seen = new HashSet<Assembly>();
        private readonly List<ManifestEntry> _members = new List<ManifestEntry>();
        private readonly List<AssemblyCoverage> _assemblies = new List<AssemblyCoverage>();
        private readonly object _recordGate = new object();

        private readonly HarmonyMethod _prefix;
        private readonly HarmonyMethod _finalizer;
        private readonly MethodInfo _postfixVoid;
        private readonly MethodInfo _postfixSync;
        private readonly MethodInfo _postfixTask;
        private readonly MethodInfo _postfixTaskOf;
        private readonly MethodInfo _postfixValueTask;
        private readonly MethodInfo _postfixValueTaskOf;

        private AssemblyLoadEventHandler? _loadHandler;
        private Thread? _drainThread;
        private volatile bool _startupComplete;
        private volatile bool _stopping;

        internal PatchInstaller(TracerOptions options, SourceLocationResolver locations, Action<MethodBase, MethodTraceInfo> register)
        {
            _options = options;
            _locations = locations;
            _register = register;

            _prefix = new HarmonyMethod(Patch(nameof(TracePatches.Prefix)));
            _finalizer = new HarmonyMethod(Patch(nameof(TracePatches.Finalizer)));
            _postfixVoid = Patch(nameof(TracePatches.PostfixVoid));
            _postfixSync = Patch(nameof(TracePatches.PostfixSync));
            _postfixTask = Patch(nameof(TracePatches.PostfixTask));
            _postfixTaskOf = Patch(nameof(TracePatches.PostfixTaskOf));
            _postfixValueTask = Patch(nameof(TracePatches.PostfixValueTask));
            _postfixValueTaskOf = Patch(nameof(TracePatches.PostfixValueTaskOf));
        }

        internal string ManifestPath { get; set; } = string.Empty;

        internal void InstallAll()
        {
            WarnIfInliningEnabled();

            // Before anything is enumerated: if Harmony cannot emit here, every patch below would fail and
            // the run would reach a downstream refusal naming the wrong cause.
            string? emitFailure = EmitProbe.Check(_harmony);
            if (emitFailure != null)
            {
                TracerDiagnostics.Write(emitFailure);
                TracerDiagnostics.WriteFailureMarker(emitFailure);
                return;
            }

            // Subscribe before enumerating. Patching a member forces its signature types to resolve, which
            // loads assemblies; one loaded that way is absent from a snapshot taken a moment earlier and
            // would never be seen at all if the subscription came afterwards.
            _loadHandler = OnAssemblyLoad;
            AppDomain.CurrentDomain.AssemblyLoad += _loadHandler;

            foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                Enqueue(assembly, AssemblyDiscovery.StartupEnumeration);
            }

            // Runs on the install thread, which is not inside a load callback. Loops until the queue is
            // empty, so assemblies pulled in by patching are covered before any test code runs.
            Drain();

            _startupComplete = true;

            _drainThread = new Thread(DrainLoop)
            {
                IsBackground = true,
                Name = "BehaviorDiff patch drain",
            };
            _drainThread.Start();

            ReportSummary();
            WriteManifest();
        }

        internal void Shutdown()
        {
            if (_loadHandler != null)
            {
                AppDomain.CurrentDomain.AssemblyLoad -= _loadHandler;
                _loadHandler = null;
            }

            _stopping = true;
            _pendingSignal.Set();
            _drainThread?.Join(TimeSpan.FromSeconds(2));

            Drain();
            ReportSourceAvailability();
            WriteManifest();
        }

        /// <summary>
        /// An assembly that produced traced calls but resolved no source line cannot have its divergences
        /// attributed. The engine would classify every one of them EXPECTED and report "no unexpected
        /// behavior changes" for code it could not analyse, so this is reported as a run-invalidating
        /// condition rather than a warning.
        /// </summary>
        private void ReportSourceAvailability()
        {
            lock (_recordGate)
            {
                // An assembly the tracer tried and wholly failed to patch is neither a coverage gap nor a
                // clean result: it is a tracer failure, and reporting it as either would be a lie.
                foreach (AssemblyCoverage failed in _assemblies)
                {
                    if (failed.PatchFailedMembers > 0 && failed.PatchedMembers == 0)
                    {
                        string message =
                            "RUN INVALID - TracerFailure: assembly '" + failed.Name + "' had "
                            + failed.PatchFailedMembers.ToString(CultureInfo.InvariantCulture)
                            + " member(s) fail to patch and 0 succeed. Nothing in it was observable, so its "
                            + "absence from the trace is a tracer failure rather than a behavioural fact.";

                        TracerDiagnostics.Write(message);
                        TracerDiagnostics.WriteFailureMarker(message);
                    }
                }

                foreach (AssemblyCoverage coverage in _assemblies)
                {
                    if (!coverage.SourceUnavailable || coverage.TracedCalls == 0)
                    {
                        continue;
                    }

                    // Harness assemblies are exempt, and not as a convenience. SourceUnavailable is
                    // run-invalidating because unattributable divergences get classified EXPECTED; the
                    // engine never reports harness divergences at all, so there is nothing to misclassify.
                    // Still recorded, because a production assembly misdetected as a test assembly would
                    // otherwise take this exit silently.
                    if (coverage.IsTestAssembly)
                    {
                        TracerDiagnostics.Write(
                            "NOTE - SourceUnavailable harness assembly '" + coverage.Name + "' at "
                            + coverage.ExactSourcePercent.ToString(CultureInfo.InvariantCulture)
                            + "% (rule " + coverage.SourceRuleApplied + ", trigger "
                            + (coverage.TestFrameworkReference ?? "?")
                            + "). Not run-invalidating: harness events are excluded from frontier candidacy.");
                        continue;
                    }

                    TracerDiagnostics.Write(
                        "RUN INVALID - SourceUnavailable: assembly '" + coverage.Name + "' produced "
                        + coverage.TracedCalls.ToString(CultureInfo.InvariantCulture)
                        + " traced call(s) but only "
                        + coverage.ExactSourcePercent.ToString(CultureInfo.InvariantCulture)
                        + "% of its patched members resolved a source line (threshold "
                        + AssemblyCoverage.ExactSourceThresholdPercent.ToString(CultureInfo.InvariantCulture)
                        + "%). Divergences in it cannot be attributed to a changed file and would be "
                        + "silently classified EXPECTED, so the run would report no unexpected behavior "
                        + "changes for an assembly it could not analyse. Remedy: build it with "
                        + "<DebugType>portable</DebugType>.");
                }
            }
        }

        private void OnAssemblyLoad(object? sender, AssemblyLoadEventArgs args)
        {
            Enqueue(args.LoadedAssembly, AssemblyDiscovery.AssemblyLoadEvent);
        }

        private void Enqueue(Assembly assembly, AssemblyDiscovery discovery)
        {
            lock (_recordGate)
            {
                if (!_seen.Add(assembly))
                {
                    return;
                }

                _assemblies.Add(new AssemblyCoverage(
                    assembly.GetName().Name ?? "<unnamed>",
                    discovery,
                    _clock.ElapsedMilliseconds));
            }

            _pending.Enqueue(assembly);
            _pendingSignal.Set();

            TracerDiagnostics.Drain("enqueue", assembly.GetName().Name ?? "<unnamed>", _clock.ElapsedMilliseconds, "discovery=" + discovery);

            if (_options.Verbose)
            {
                TracerDiagnostics.Write("queued " + (assembly.GetName().Name ?? "<unnamed>") + " (" + discovery + ")");
            }
        }

        private void DrainLoop()
        {
            while (!_stopping)
            {
                _pendingSignal.Wait(250);
                _pendingSignal.Reset();
                Drain();
            }
        }

        private void Drain()
        {
            lock (_drainGate)
            {
                while (_pending.TryDequeue(out Assembly? assembly))
                {
                    string name = assembly.GetName().Name ?? "<unnamed>";
                    TracerDiagnostics.Drain("patch-begin", name, _clock.ElapsedMilliseconds);
                    PatchAssembly(assembly);
                    TracerDiagnostics.Drain("patch-end", name, _clock.ElapsedMilliseconds);
                }
            }
        }

        private AssemblyCoverage CoverageFor(Assembly assembly)
        {
            string name = assembly.GetName().Name ?? "<unnamed>";
            lock (_recordGate)
            {
                for (int i = _assemblies.Count - 1; i >= 0; i--)
                {
                    if (string.Equals(_assemblies[i].Name, name, StringComparison.Ordinal))
                    {
                        return _assemblies[i];
                    }
                }

                var coverage = new AssemblyCoverage(name, AssemblyDiscovery.AssemblyLoadEvent, _clock.ElapsedMilliseconds);
                _assemblies.Add(coverage);
                return coverage;
            }
        }

        private void PatchAssembly(Assembly assembly)
        {
            AssemblyCoverage coverage = CoverageFor(assembly);

            if (MethodSelector.IsTestAssembly(assembly, out string? trigger))
            {
                coverage.IsTestAssembly = true;
                coverage.TestFrameworkReference = trigger;
            }

            if (!MethodSelector.IsCandidateAssembly(assembly, _options))
            {
                coverage.Detail = "out of scope";
                coverage.MarkComplete(_clock.ElapsedMilliseconds, afterStartup: false, scanned: false);
                return;
            }

            Type[] types;
            try
            {
                types = assembly.GetTypes();
            }
            catch (ReflectionTypeLoadException ex)
            {
                var loaded = new List<Type>();
                foreach (Type? type in ex.Types)
                {
                    if (type != null)
                    {
                        loaded.Add(type);
                    }
                }

                types = loaded.ToArray();
                RecordEnumerationFailure(coverage.Name, ex);
            }
            catch (Exception ex)
            {
                // Never silent: an unreadable assembly is missing coverage, which downstream reads as a
                // behavior change.
                RecordEnumerationFailure(coverage.Name, ex);
                coverage.Detail = ex.GetType().Name + ": " + ex.Message;
                coverage.MarkComplete(_clock.ElapsedMilliseconds, _startupComplete, scanned: false);
                return;
            }

            if (_options.Verbose)
            {
                TracerDiagnostics.Write(
                    "scanning " + coverage.Name + " ("
                    + types.Length.ToString(CultureInfo.InvariantCulture) + " types)");
            }

            foreach (Type type in types)
            {
                if (!MethodSelector.IsInScope(type, _options))
                {
                    continue;
                }

                PatchType(type, coverage);
            }

            coverage.MarkComplete(_clock.ElapsedMilliseconds, _startupComplete, scanned: true);

            if (_startupComplete)
            {
                TracerDiagnostics.Write(
                    "LatePatched " + coverage.Name + " at "
                    + _clock.ElapsedMilliseconds.ToString(CultureInfo.InvariantCulture)
                    + "ms; calls into it may have run before instrumentation completed");
            }
        }

        private void PatchType(Type type, AssemblyCoverage coverage)
        {
            SkipReason typeReason = MethodSelector.EvaluateType(type, _options, MethodSelector.Backend.Harmony);

            List<MethodBase> members;
            try
            {
                members = new List<MethodBase>();
                members.AddRange(type.GetMethods(MemberFlags));

                // Constructors are traced for their arguments: configuration captured into object state at
                // construction is otherwise visible only through its downstream effects, with no record of
                // where the value entered.
                members.AddRange(type.GetConstructors(MemberFlags));
            }
            catch (Exception ex)
            {
                RecordEnumerationFailure(coverage.Name, ex);
                return;
            }

            foreach (MethodBase member in members)
            {
                // A type-level skip still yields a manifest entry per member: the engine has to know these
                // exist and are unobservable, or the frontier rule silently loses its footing.
                SkipReason reason = typeReason != SkipReason.None ? typeReason : MethodSelector.Evaluate(member, MethodSelector.Backend.Harmony);

                if (reason != SkipReason.None)
                {
                    RecordSkip(coverage.Name, member, reason);
                    continue;
                }

                PatchMember(member, coverage);
            }
        }

        private void PatchMember(MethodBase member, AssemblyCoverage coverage)
        {
            ReturnKind kind = MethodSelector.ClassifyReturn(member);
            bool isTestRoot = MethodSelector.IsTestRoot(member, _options.TestAttributeNames);
            ParameterInfo[] parameters = member.GetParameters();
            string fullName = MethodSelector.BuildFullName(member, parameters);

            MethodInfo postfix;
            try
            {
                postfix = SelectPostfix(kind, member);
            }
            catch (Exception ex)
            {
                RecordFailure(coverage.Name, fullName, kind, isTestRoot, ex);
                return;
            }

            var parameterNames = new string[parameters.Length];
            for (int i = 0; i < parameters.Length; i++)
            {
                parameterNames[i] = parameters[i].Name ?? ("arg" + i.ToString(CultureInfo.InvariantCulture));
            }

            _locations.Resolve(member, out string? filePath, out int line, out string sourceResolution);
            var info = new MethodTraceInfo(fullName, filePath, line, sourceResolution, kind, parameterNames, coverage, isTestRoot);

            try
            {
                // Register before patching: a call can arrive the instant the detour is live.
                _register(member, info);
                _harmony.Patch(member, prefix: _prefix, postfix: new HarmonyMethod(postfix), finalizer: _finalizer);

                // Patch() not throwing is not proof the detour took. A manifest that claims coverage it
                // does not have is worse than no manifest, so confirm the prefix is actually registered.
                if (!IsPatchRegistered(member))
                {
                    RecordMember(new ManifestEntry
                    {
                        Assembly = coverage.Name,
                        MethodFullName = fullName,
                        Status = PatchStatus.PatchFailed,
                        ReturnKind = kind.ToString(),
                        IsTestRoot = isTestRoot,
                        Detail = "Harmony reported no registered prefix after patching",
                    });

                    TracerDiagnostics.Write("patch did not register for " + fullName);
                    coverage.NotePatchFailed();
                    return;
                }

                RecordMember(new ManifestEntry
                {
                    Assembly = coverage.Name,
                    MethodFullName = fullName,
                    Status = PatchStatus.Patched,
                    ReturnKind = kind.ToString(),
                    IsTestRoot = isTestRoot,
                    SourceResolution = sourceResolution,
                });

                coverage.NotePatchedMember();
                coverage.NoteMemberSource(sourceResolution);

                if (!Contracts.SourceResolution.IsUsable(sourceResolution))
                {
                    TracerDiagnostics.Write(
                        "no source path for " + fullName + " (" + sourceResolution
                        + "); the engine cannot attribute divergences in this member to a changed file");
                }

                if (_options.Verbose)
                {
                    TracerDiagnostics.Write("patched " + fullName + " [" + kind + (isTestRoot ? ", test root" : string.Empty) + "]");
                }
            }
            catch (Exception ex)
            {
                coverage.NotePatchFailed();
                RecordFailure(coverage.Name, fullName, kind, isTestRoot, ex);
            }
        }

        private bool IsPatchRegistered(MethodBase member)
        {
            try
            {
                Patches? patches = Harmony.GetPatchInfo(member);
                if (patches is null)
                {
                    return false;
                }

                foreach (Patch patch in patches.Prefixes)
                {
                    if (string.Equals(patch.owner, _harmony.Id, StringComparison.Ordinal))
                    {
                        return true;
                    }
                }

                return false;
            }
            catch (Exception)
            {
                return false;
            }
        }

        private MethodInfo SelectPostfix(ReturnKind kind, MethodBase member)
        {
            switch (kind)
            {
                case ReturnKind.Void:
                    return _postfixVoid;
                case ReturnKind.Task:
                    return _postfixTask;
                case ReturnKind.TaskOfT:
                    return _postfixTaskOf.MakeGenericMethod(ReturnTypeOf(member).GetGenericArguments()[0]);
                case ReturnKind.ValueTask:
                    return _postfixValueTask;
                case ReturnKind.ValueTaskOfT:
                    return _postfixValueTaskOf.MakeGenericMethod(ReturnTypeOf(member).GetGenericArguments()[0]);
                default:
                    return _postfixSync.MakeGenericMethod(ReturnTypeOf(member));
            }
        }

        private static Type ReturnTypeOf(MethodBase member)
        {
            return member is MethodInfo methodInfo ? methodInfo.ReturnType : typeof(void);
        }

        private static MethodInfo Patch(string name)
        {
            MethodInfo? method = typeof(TracePatches).GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic);
            if (method is null)
            {
                throw new InvalidOperationException("Patch method not found: " + name);
            }

            return method;
        }

        private void WarnIfInliningEnabled()
        {
            if (Environment.GetEnvironmentVariable(JitMinOptsVariable) == "1")
            {
                return;
            }

            TracerDiagnostics.Write(
                JitMinOptsVariable + " is not set to 1. The JIT will inline small methods, whose calls then "
                + "bypass the patch and produce no events even though this manifest reports them as Patched. "
                + "Inlining decisions can differ between the two builds being compared, so this shows up as a "
                + "false behavior difference. Note that DOTNET_JitNoInline is a checked-build knob and has no "
                + "effect on a retail runtime. Set " + JitMinOptsVariable + "=1 before starting the process.");
        }

        private void RecordMember(ManifestEntry entry)
        {
            lock (_recordGate)
            {
                _members.Add(entry);
            }
        }

        private void RecordSkip(string assemblyName, MethodBase member, SkipReason reason)
        {
            RecordMember(new ManifestEntry
            {
                Assembly = assemblyName,
                MethodFullName = MethodSelector.BuildFullName(member, member.GetParameters()),
                Status = PatchStatus.Skipped,
                SkipReason = reason.ToString(),
            });

            if (_options.Verbose)
            {
                TracerDiagnostics.Write("skipped " + member.DeclaringType?.FullName + "." + member.Name + " (" + reason + ")");
            }
        }

        private void RecordFailure(string assemblyName, string fullName, ReturnKind kind, bool isTestRoot, Exception ex)
        {
            RecordMember(new ManifestEntry
            {
                Assembly = assemblyName,
                MethodFullName = fullName,
                Status = PatchStatus.PatchFailed,
                ReturnKind = kind.ToString(),
                IsTestRoot = isTestRoot,
                Detail = ex.GetType().Name + ": " + ex.Message,
            });

            TracerDiagnostics.Write("failed to patch " + fullName + " -> " + ex.GetType().Name + ": " + ex.Message);
        }

        private void RecordEnumerationFailure(string assemblyName, Exception ex)
        {
            RecordMember(new ManifestEntry
            {
                Assembly = assemblyName,
                Status = PatchStatus.EnumerationFailed,
                Detail = ex.GetType().Name + ": " + ex.Message,
            });

            TracerDiagnostics.Write("could not enumerate types in " + assemblyName + " -> " + ex.GetType().Name + ": " + ex.Message);
        }

        private void WriteManifest()
        {
            if (string.IsNullOrEmpty(ManifestPath))
            {
                return;
            }

            CoverageManifest manifest;
            lock (_recordGate)
            {
                var assemblies = new List<AssemblyManifestEntry>(_assemblies.Count);
                foreach (AssemblyCoverage coverage in _assemblies)
                {
                    assemblies.Add(coverage.ToManifestEntry());
                }

                manifest = new CoverageManifest
                {
                    Assemblies = assemblies,
                    Members = new List<ManifestEntry>(_members),
                    UnruledEnumerables = BuildUnruled(),
                    DigestStats = new DigestStatsEntry
                    {
                        ValuesDigested = DigestStatistics.ValuesDigested,
                        DepthLimited = DigestStatistics.DepthLimited,
                        Blocklisted = DigestStatistics.Blocklisted,
                        Errored = DigestStatistics.Errored,
                        RenderedTruncated = DigestStatistics.RenderedTruncated,
                    },
                };
            }

            try
            {
                ManifestFile.Write(ManifestPath, manifest);
            }
            catch (Exception ex)
            {
                TracerDiagnostics.Write("could not write manifest -> " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        private static List<UnruledEnumerableEntry> BuildUnruled()
        {
            var entries = new List<UnruledEnumerableEntry>();
            foreach (KeyValuePair<string, long> entry in DigestStatistics.UnruledEnumerables())
            {
                entries.Add(new UnruledEnumerableEntry { TypeName = entry.Key, Count = entry.Value });
            }

            return entries;
        }

        private void ReportSummary()
        {
            var counts = new Dictionary<string, int>(StringComparer.Ordinal);
            int patched = 0;
            int failed = 0;
            int enumerationFailures = 0;
            int total;

            lock (_recordGate)
            {
                total = _members.Count;
                foreach (ManifestEntry entry in _members)
                {
                    switch (entry.Status)
                    {
                        case PatchStatus.Patched:
                            patched++;
                            break;
                        case PatchStatus.PatchFailed:
                            failed++;
                            break;
                        case PatchStatus.EnumerationFailed:
                            enumerationFailures++;
                            break;
                        default:
                            string reason = entry.SkipReason ?? "Unknown";
                            counts.TryGetValue(reason, out int count);
                            counts[reason] = count + 1;
                            break;
                    }
                }
            }

            var builder = new StringBuilder(160);
            builder.Append("discovered ").Append(total.ToString(CultureInfo.InvariantCulture))
                .Append(" member(s): ").Append(patched.ToString(CultureInfo.InvariantCulture)).Append(" patched");

            if (failed > 0)
            {
                builder.Append(", ").Append(failed.ToString(CultureInfo.InvariantCulture)).Append(" PatchFailed");
            }

            if (enumerationFailures > 0)
            {
                builder.Append(", ").Append(enumerationFailures.ToString(CultureInfo.InvariantCulture)).Append(" EnumerationFailed");
            }

            foreach (KeyValuePair<string, int> entry in counts)
            {
                builder.Append(", ").Append(entry.Value.ToString(CultureInfo.InvariantCulture)).Append(' ').Append(entry.Key);
            }

            TracerDiagnostics.Write(builder.ToString());
        }
    }
}
