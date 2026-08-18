using System;
using System.Reflection;
using System.Threading.Tasks;

namespace BehaviorDiff.Tracer
{
    /// <summary>
    /// The Harmony patch bodies. One prefix and one finalizer serve every patched method; the postfix is
    /// chosen per return kind because Harmony matches <c>__result</c> against the real return type.
    /// </summary>
    internal static class TracePatches
    {
        internal static void Prefix(MethodBase __originalMethod, object[] __args, out object? __state)
        {
            __state = TraceSession.BeginCall(__originalMethod, __args);
        }

        internal static void PostfixVoid(object? __state)
        {
            if (__state is CallFrame frame)
            {
                TraceSession.CompleteSync(frame, result: null);
            }
        }

        internal static void PostfixSync<T>(object? __state, T __result)
        {
            if (__state is CallFrame frame)
            {
                TraceSession.CompleteSync(frame, TraceSession.Render(__result));
            }
        }

        /// <summary>
        /// Non-generic <see cref="Task"/>. The postfix runs when the state machine first yields, so the
        /// event is deferred to a continuation on the returned task rather than emitted here.
        /// </summary>
        internal static void PostfixTask(object? __state, Task __result)
        {
            if (__state is CallFrame frame)
            {
                TraceSession.AttachContinuation(frame, __result, resultRenderer: null);
            }
        }

        internal static void PostfixTaskOf<T>(object? __state, Task<T> __result)
        {
            if (__state is CallFrame frame)
            {
                TraceSession.AttachContinuation(frame, __result, static completed => TraceSession.Render(((Task<T>)completed).Result));
            }
        }

        /// <summary>
        /// <see cref="ValueTask"/> may only be consumed once when it is backed by an
        /// <c>IValueTaskSource</c>. <c>AsTask()</c> performs that single consumption, and the caller is
        /// handed a <see cref="ValueTask"/> over the resulting <see cref="Task"/>, which is safe to await
        /// repeatedly. Observing the original value task and also returning it would be a use-after-consume.
        /// </summary>
        internal static void PostfixValueTask(object? __state, ref ValueTask __result)
        {
            if (__state is not CallFrame frame)
            {
                return;
            }

            Task task = __result.AsTask();
            __result = new ValueTask(task);
            TraceSession.AttachContinuation(frame, task, resultRenderer: null);
        }

        /// <inheritdoc cref="PostfixValueTask" />
        internal static void PostfixValueTaskOf<T>(object? __state, ref ValueTask<T> __result)
        {
            if (__state is not CallFrame frame)
            {
                return;
            }

            Task<T> task = __result.AsTask();
            __result = new ValueTask<T>(task);
            TraceSession.AttachContinuation(frame, task, static completed => TraceSession.Render(((Task<T>)completed).Result));
        }

        /// <summary>
        /// Runs on both the success and the throw path, unlike a postfix. Restores the call frame and, when
        /// the method threw synchronously, emits the event the postfix never got to produce.
        /// </summary>
        internal static void Finalizer(object? __state, Exception? __exception)
        {
            if (__state is CallFrame frame)
            {
                TraceSession.EndCall(frame, __exception);
            }
        }
    }
}
