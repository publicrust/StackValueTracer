using System;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using HarmonyLib;

namespace StackValueTracer;

internal static class TraceHooks
{
    internal static readonly HarmonyMethod Prefix = new(typeof(TraceHooks).GetMethod(nameof(PrefixImpl), BindingFlags.Static | BindingFlags.NonPublic) ?? throw new InvalidOperationException());
    internal static readonly HarmonyMethod AsyncPostfix = new(typeof(TraceHooks).GetMethod(nameof(AsyncPostfixImpl), BindingFlags.Static | BindingFlags.NonPublic) ?? throw new InvalidOperationException());
    internal static readonly HarmonyMethod Finalizer = new(typeof(TraceHooks).GetMethod(nameof(FinalizerImpl), BindingFlags.Static | BindingFlags.NonPublic) ?? throw new InvalidOperationException());

    private static void PrefixImpl(object?[]? __args, MethodBase __originalMethod, ref ValueStackScope __state)
    {
        __state = ValueStack.Enter(__originalMethod, __args);
    }

    private static void AsyncPostfixImpl(MethodBase __originalMethod, object? __result, ref ValueStackScope __state)
    {
        if (!__state.IsActive)
        {
            return;
        }

        if (__originalMethod is MethodInfo methodInfo && TryHandleAsyncReturn(methodInfo, __result, __state))
        {
            return;
        }

        __state.Dispose();
    }

    private static Exception? FinalizerImpl(Exception? __exception, ref ValueStackScope __state)
    {
        if (!__state.IsActive)
        {
            return __exception;
        }

        if (__exception is not null)
        {
            ValueStack.CaptureSnapshot();
            __state.Dispose();
            return __exception;
        }

        if (!__state.IsDeferred)
        {
            __state.Dispose();
        }

        return __exception;
    }

    private static bool TryHandleAsyncReturn(MethodInfo methodInfo, object? result, ValueStackScope scope)
    {
        if (result is Task task)
        {
            SchedulePopOnTaskCompletion(task, scope);
            return true;
        }

        var returnType = methodInfo.ReturnType;
        if (returnType == typeof(ValueTask))
        {
            if (result is ValueTask valueTask)
            {
                if (valueTask.IsCompleted)
                {
                    scope.Dispose();
                }
                else
                {
                    SchedulePopOnTaskCompletion(valueTask.AsTask(), scope);
                }
            }
            else
            {
                scope.Dispose();
            }

            return true;
        }

        if (returnType.IsGenericType && returnType.GetGenericTypeDefinition() == typeof(ValueTask<>))
        {
            if (result is null)
            {
                scope.Dispose();
                return true;
            }

            var awaitedTask = InvokeAsTask(result);
            if (awaitedTask is null)
            {
                scope.Dispose();
                return true;
            }

            SchedulePopOnTaskCompletion(awaitedTask, scope);
            return true;
        }

        return false;
    }

    private static void SchedulePopOnTaskCompletion(Task task, ValueStackScope scope)
    {
        if (task.IsCompleted)
        {
            if (task.IsFaulted || task.IsCanceled)
            {
                ValueStack.CaptureSnapshot();
            }

            scope.Dispose();
            return;
        }

        scope.Defer();
        task.ContinueWith(static (completedTask, state) =>
        {
            if (completedTask.IsFaulted || completedTask.IsCanceled)
            {
                ValueStack.CaptureSnapshot();
            }

            ((ValueStackScope)state!).Dispose();
        }, scope, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    private static Task? InvokeAsTask(object valueTask)
    {
        var method = valueTask.GetType().GetMethod("AsTask", BindingFlags.Instance | BindingFlags.Public);
        if (method is null)
        {
            return null;
        }

        return method.Invoke(valueTask, Array.Empty<object?>()) as Task;
    }
}
