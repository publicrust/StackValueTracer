using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace StackValueTracer;

/// <summary>
/// Configuration for the Harmony-based value stack tracer.
/// </summary>
public sealed class TraceOptions
{
    public TraceOptions()
    {
    }

    public TraceOptions(params Assembly[] assemblies)
    {
        Assemblies = assemblies;
    }

    public IReadOnlyCollection<Assembly>? Assemblies { get; init; }

    public string? HarmonyId { get; init; }

    public Func<MethodBase, bool>? MethodFilter { get; init; }

    internal IEnumerable<Assembly> ResolveAssemblies()
    {
        if (Assemblies is { Count: > 0 })
        {
            return Assemblies.Where(a => a is not null)!;
        }

        var entry = Assembly.GetEntryAssembly();
        if (entry is not null)
        {
            return new[] { entry };
        }

        var executing = Assembly.GetExecutingAssembly();
        return new[] { executing };
    }
}
