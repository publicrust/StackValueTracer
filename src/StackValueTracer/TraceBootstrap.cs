using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;

namespace StackValueTracer;

/// <summary>
/// Entry point for enabling Harmony-based value stack tracing.
/// </summary>
public static class TraceBootstrap
{
    private static readonly object SyncRoot = new();
    private static readonly HashSet<MethodBase> PatchedMethods = new();
    private static Harmony? _harmony;

    public static void Enable(params Assembly[] assemblies)
    {
        Enable(new TraceOptions(assemblies));
    }

    public static void Enable(TraceOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var targetAssemblies = options.ResolveAssemblies().ToArray();
        if (targetAssemblies.Length == 0)
        {
            return;
        }

        lock (SyncRoot)
        {
            _harmony ??= new Harmony(options.HarmonyId ?? $"StackValueTracer.{targetAssemblies[0].GetName().Name}");

            foreach (var method in TraceTargetDiscovery.FindMethods(targetAssemblies, options.MethodFilter))
            {
                if (!PatchedMethods.Add(method))
                {
                    continue;
                }

                if (method is MethodInfo methodInfo && TraceTargetDiscovery.IsAsyncLike(methodInfo))
                {
                    _harmony.Patch(method, TraceHooks.Prefix, postfix: TraceHooks.AsyncPostfix, finalizer: TraceHooks.Finalizer);
                }
                else
                {
                    _harmony.Patch(method, TraceHooks.Prefix, finalizer: TraceHooks.Finalizer);
                }
            }
        }
    }
}
