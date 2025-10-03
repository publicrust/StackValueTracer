namespace StackValueTracer;

/// <summary>
/// Marks methods or classes that should be instrumented by the Harmony trace patch.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, Inherited = false, AllowMultiple = false)]
public sealed class TraceValuesAttribute : Attribute
{
}
