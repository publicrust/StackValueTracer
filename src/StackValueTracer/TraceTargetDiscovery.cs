using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading.Tasks;

namespace StackValueTracer;

internal static class TraceTargetDiscovery
{
    private const BindingFlags MethodFlags = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;

    public static IEnumerable<MethodBase> FindMethods(IEnumerable<Assembly> assemblies, Func<MethodBase, bool>? additionalFilter)
    {
        foreach (var assembly in assemblies)
        {
            if (assembly is null)
            {
                continue;
            }

            foreach (var type in GetLoadableTypes(assembly))
            {
                if (type is null || type.IsInterface)
                {
                    continue;
                }

                var typeMarked = type.IsDefined(typeof(TraceValuesAttribute), inherit: false);

                foreach (var method in type.GetMethods(MethodFlags))
                {
                    if (!ShouldInstrument(method, typeMarked))
                    {
                        continue;
                    }

                    if (additionalFilter is not null && !additionalFilter(method))
                    {
                        continue;
                    }

                    yield return method;
                }
            }
        }
    }

    private static bool ShouldInstrument(MethodInfo method, bool typeMarked)
    {
        if (method.IsAbstract || method.IsGenericMethodDefinition || method.ContainsGenericParameters)
        {
            return false;
        }

        if (method.IsSpecialName)
        {
            return false;
        }

        if (!typeMarked && !method.IsDefined(typeof(TraceValuesAttribute), inherit: false))
        {
            return false;
        }

        return true;
    }

    public static bool IsAsyncLike(MethodInfo method)
    {
        var returnType = method.ReturnType;

        if (returnType == typeof(void))
        {
            return false;
        }

        if (returnType == typeof(Task) || returnType == typeof(ValueTask))
        {
            return true;
        }

        if (typeof(Task).IsAssignableFrom(returnType))
        {
            return true;
        }

        if (returnType.IsGenericType && returnType.GetGenericTypeDefinition() == typeof(ValueTask<>))
        {
            return true;
        }

        return false;
    }

    private static IEnumerable<Type?> GetLoadableTypes(Assembly assembly)
    {
        try
        {
            return assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            return ex.Types;
        }
    }
}
