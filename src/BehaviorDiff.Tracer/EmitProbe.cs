using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using HarmonyLib;

namespace BehaviorDiff.Tracer
{
    /// <summary>
    /// Verifies Harmony can actually emit on this runtime before anything is enumerated.
    /// </summary>
    /// <remarks>
    /// Without this, a runtime Harmony cannot patch produces thousands of PatchFailed manifest entries and
    /// zero patched members, and the run continues to a downstream volume refusal that names the wrong
    /// cause. Observed on .NET 9: MonoMod constructs System.Reflection.Emit.LocalBuilder, which became
    /// abstract, so every patch throws MemberAccessException. One throwaway patch answers that in
    /// milliseconds and lets the failure be reported where it happens.
    /// </remarks>
    internal static class EmitProbe
    {
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static int Target() => 1;

        internal static void Postfix(ref int __result)
        {
            __result = 2;
        }

        /// <summary>Returns null when emit works, or a description of why it does not.</summary>
        internal static string? Check(Harmony harmony)
        {
            System.Reflection.MethodInfo target = AccessTools.Method(typeof(EmitProbe), nameof(Target));
            System.Reflection.MethodInfo postfix = AccessTools.Method(typeof(EmitProbe), nameof(Postfix));

            try
            {
                harmony.Patch(target, postfix: new HarmonyMethod(postfix));

                Patches? info = Harmony.GetPatchInfo(target);
                if (info is null || info.Postfixes.Count == 0)
                {
                    return Describe("Harmony reported no registered postfix on the probe method.");
                }

                if (Target() != 2)
                {
                    return Describe("The probe patch registered but the detour did not take effect.");
                }

                harmony.Unpatch(target, postfix);
                return null;
            }
            catch (Exception ex)
            {
                return Describe(ex.GetType().Name + ": " + ex.Message);
            }
        }

        private static string Describe(string detail)
        {
            string harmonyVersion = typeof(Harmony).Assembly.GetName().Version?.ToString() ?? "unknown";

            return "RUN INVALID - TracerCannotEmit: Harmony cannot rewrite methods on this runtime, so no "
                + "member can be instrumented and no trace would be produced." + Environment.NewLine
                + "  runtime : " + RuntimeInformation.FrameworkDescription + Environment.NewLine
                + "  harmony : " + harmonyVersion + Environment.NewLine
                + "  detail  : " + detail + Environment.NewLine
                + "  Remedy: run the suite on a target framework this Harmony version supports, or upgrade "
                + "Lib.Harmony.";
        }
    }
}
