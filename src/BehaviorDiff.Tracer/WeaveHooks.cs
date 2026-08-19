using System;
using BehaviorDiff.Contracts;

namespace BehaviorDiff.Tracer
{
    /// <summary>
    /// Entry points called from woven IL. Public because instrumented assemblies reference them directly,
    /// unlike the Harmony path where reflection reaches internal patch bodies.
    /// </summary>
    /// <remarks>
    /// Exit ordering mirrors <see cref="TracePatches"/> exactly: on the success path the return value is
    /// recorded and then the event is emitted; on the throw path only the emit runs. The event is produced
    /// at method exit under both backends, which is what makes per-key ordinals comparable between them.
    /// </remarks>
    public static class WeaveHooks
    {
        private static readonly object s_gate = new object();
        private static readonly string[] s_noParameters = new string[0];

        // Read without a lock on the hot path; replaced wholesale under the lock when it grows.
        private static MethodTraceInfo?[] s_descriptors = new MethodTraceInfo?[0];

        private static AssemblyCoverage?[] s_assemblies = new AssemblyCoverage?[0];

        /// <summary>Declares a woven assembly. Called from the woven module initializer before any Register.</summary>
        public static int RegisterAssembly(string assemblyName, bool isTestAssembly)
        {
            if (assemblyName == null)
            {
                throw new ArgumentNullException(nameof(assemblyName));
            }

            lock (s_gate)
            {
                var coverage = new AssemblyCoverage(assemblyName, AssemblyDiscovery.BuildTimeWeave, queuedAtMs: 0)
                {
                    IsTestAssembly = isTestAssembly,
                };

                int index = s_assemblies.Length;
                var grown = new AssemblyCoverage?[index + 1];
                Array.Copy(s_assemblies, grown, index);
                grown[index] = coverage;
                s_assemblies = grown;
                return index;
            }
        }

        /// <summary>Declares one woven method. The index is a dense slot the call site passes back to <see cref="Enter"/>.</summary>
        public static void Register(
            int index,
            int assemblyIndex,
            string fullName,
            string? filePath,
            int line,
            string sourceResolution,
            int returnKind,
            string parameterNames)
        {
            if (index < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(index));
            }

            if (!Enum.IsDefined(typeof(ReturnKind), returnKind))
            {
                throw new ArgumentOutOfRangeException(nameof(returnKind), returnKind, "not a ReturnKind value");
            }

            lock (s_gate)
            {
                if (assemblyIndex < 0 || assemblyIndex >= s_assemblies.Length)
                {
                    throw new ArgumentOutOfRangeException(nameof(assemblyIndex));
                }

                AssemblyCoverage coverage = s_assemblies[assemblyIndex]!;

                if (index >= s_descriptors.Length)
                {
                    var grown = new MethodTraceInfo?[index + 1];
                    Array.Copy(s_descriptors, grown, s_descriptors.Length);
                    s_descriptors = grown;
                }

                if (s_descriptors[index] != null)
                {
                    throw new InvalidOperationException("duplicate weave index " + index + " for " + fullName);
                }

                s_descriptors[index] = new MethodTraceInfo(
                    fullName,
                    filePath,
                    line,
                    sourceResolution,
                    (ReturnKind)returnKind,
                    parameterNames.Length == 0 ? s_noParameters : parameterNames.Split(','),
                    coverage);

                coverage.NotePatchedMember();
                coverage.NoteMemberSource(sourceResolution);
            }
        }

        /// <summary>Method prologue. The returned frame must be stored in a local that survives into the handler.</summary>
        public static object? Enter(int index, object[] args)
        {
            MethodTraceInfo?[] descriptors = s_descriptors;
            if ((uint)index >= (uint)descriptors.Length)
            {
                return null;
            }

            MethodTraceInfo? info = descriptors[index];
            return info == null ? null : TraceSession.BeginCall(info, args);
        }

        /// <summary>Normal return from a value-returning method.</summary>
        public static void ExitValue(object? frame, object? result)
        {
            if (frame is CallFrame call)
            {
                TraceSession.CompleteSync(call, TraceSession.Render(result));
                TraceSession.EndCall(call, exception: null);
            }
        }

        /// <summary>Normal return from a void method.</summary>
        public static void ExitVoid(object? frame)
        {
            if (frame is CallFrame call)
            {
                TraceSession.CompleteSync(call, result: null);
                TraceSession.EndCall(call, exception: null);
            }
        }

        /// <summary>Escaping exception. No return value is recorded, matching the Harmony finalizer.</summary>
        public static void ExitException(object? frame, Exception exception)
        {
            if (frame is CallFrame call)
            {
                TraceSession.EndCall(call, exception);
            }
        }

        internal static AssemblyCoverage[] Assemblies()
        {
            lock (s_gate)
            {
                var live = new System.Collections.Generic.List<AssemblyCoverage>(s_assemblies.Length);
                foreach (AssemblyCoverage? coverage in s_assemblies)
                {
                    if (coverage != null)
                    {
                        live.Add(coverage);
                    }
                }

                return live.ToArray();
            }
        }
    }
}
