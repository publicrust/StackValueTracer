using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Diagnostics;

namespace StackValueTracer;

/// <summary>
/// Maintains a logical call stack with argument values captured for each instrumented method.
/// </summary>
public static class ValueStack
{
    private const int EnumerablePreviewCount = 5;
    private const int SnippetContextLines = 3;

    private static readonly AsyncLocal<StackContext?> ContextHolder = new();

    internal static ValueStackScope Enter(MethodBase? method, object?[]? args)
    {
        if (method is null)
        {
            return new ValueStackScope(false);
        }

        var context = ContextHolder.Value;
        if (context is null)
        {
            context = new StackContext();
            ContextHolder.Value = context;
        }
        else if (context.Frames.Count == 0)
        {
            context.Snapshot = null;
        }

        var capturedArgs = CaptureArguments(method, args ?? Array.Empty<object?>());
        var signature = BuildSignature(method, capturedArgs);
        var source = SourceInfoProvider.TryGetSourceInfo(method, SnippetContextLines);

        context.Frames.Push(new ValueStackFrame(signature, capturedArgs, source));
        return new ValueStackScope(true);
    }

    internal static void Pop()
    {
        var context = ContextHolder.Value;
        if (context?.Frames is { Count: > 0 })
        {
            context.Frames.Pop();
        }
    }

    internal static void CaptureSnapshot()
    {
        var context = ContextHolder.Value;
        if (context is null || context.Frames.Count == 0 || context.Snapshot is { Length: > 0 })
        {
            return;
        }

        var frames = context.Frames.ToArray();
        Array.Reverse(frames);
        context.Snapshot = frames;
    }

    public static string Dump(bool clear = false)
    {
        var context = ContextHolder.Value;
        if (context is null)
        {
            return string.Empty;
        }

        var frames = CollectFrames(context);
        if (frames is null || frames.Count == 0)
        {
            return string.Empty;
        }

        var callSiteFrame = CreateCallSiteFrame();
        if (callSiteFrame is not null)
        {
            frames.Add(callSiteFrame);
        }

        frames = TrimFrames(frames);

        var builder = new StringBuilder();
        for (var index = 0; index < frames.Count; index++)
        {
            AppendFrame(builder, frames[index], index);
        }

        if (clear)
        {
            Clear();
        }

        return builder.ToString();
    }

    public static void Clear()
    {
        var context = ContextHolder.Value;
        if (context is null)
        {
            return;
        }

        context.Frames.Clear();
        context.Snapshot = null;
    }

    private static List<ValueStackFrame>? CollectFrames(StackContext context)
    {
        ValueStackFrame[]? frames = null;

        if (context.Frames.Count > 0)
        {
            frames = context.Frames.ToArray();
            Array.Reverse(frames);
        }
        else
        {
            frames = context.Snapshot;
        }

        if (frames is null)
        {
            return null;
        }

        return frames.ToList();
    }

    private static List<ValueStackFrame> TrimFrames(List<ValueStackFrame> frames)
    {
        const int maxFrames = 5;
        if (frames.Count <= maxFrames)
        {
            return frames;
        }

        return frames.GetRange(frames.Count - maxFrames, maxFrames);
    }

    private static IReadOnlyList<ArgumentCapture> CaptureArguments(MethodBase method, IReadOnlyList<object?> args)
    {
        var parameters = method.GetParameters();
        if (parameters.Length == 0)
        {
            return Array.Empty<ArgumentCapture>();
        }

        var result = new ArgumentCapture[parameters.Length];
        for (var i = 0; i < parameters.Length; i++)
        {
            var name = parameters[i].Name ?? $"arg{i}";
            var value = i < args.Count ? FormatValue(args[i]) : "<missing>";
            result[i] = new ArgumentCapture(name, value);
        }

        return result;
    }

    private static string BuildSignature(MethodBase method, IReadOnlyList<ArgumentCapture> arguments)
    {
        var declaringType = method.DeclaringType?.FullName ?? "<global>";
        var methodName = method.Name;
        var renderedArgs = arguments.Count == 0
            ? string.Empty
            : string.Join(", ", arguments.Select(arg => $"{arg.Name}={arg.Display}"));

        return arguments.Count == 0
            ? $"{declaringType}.{methodName}()"
            : $"{declaringType}.{methodName}({renderedArgs})";
    }

    private static void AppendFrame(StringBuilder builder, ValueStackFrame frame, int index)
    {
        builder.Append(index + 1).Append('.').Append(' ');

        if (frame.Source is { } source)
        {
            builder.Append(Path.GetFileName(source.FilePath))
                   .Append(':').Append(source.Line);

            if (source.Column > 0)
            {
                builder.Append(':').Append(source.Column);
            }

            builder.Append(" → ");
        }
        else
        {
            builder.Append("→ ");
        }

        builder.AppendLine(frame.Signature);

        if (frame.Source?.Snippet is { Count: > 0 } snippet)
        {
            const string snippetIndent = "      ";
            foreach (var line in snippet)
            {
                builder.Append(snippetIndent);
                builder.Append(line.IsPrimary ? "> " : "  ");
                builder.Append(line.LineNumber.ToString(CultureInfo.InvariantCulture).PadLeft(3));
                builder.Append("  ");
                builder.AppendLine(line.Text);
            }
        }

        builder.AppendLine();
    }

    private static string FormatValue(object? value)
    {
        switch (value)
        {
            case null:
                return "null";
            case string s:
                return $"\"{s}\"";
            case char c:
                return $"'{c}'";
            case IEnumerable enumerable when value is not string:
                return FormatEnumerable(enumerable);
            case IFormattable formattable:
                return formattable.ToString(null, CultureInfo.InvariantCulture) ?? value.ToString() ?? value.GetType().Name;
            default:
                return value.ToString() ?? value.GetType().Name;
        }
    }

    private static string FormatEnumerable(IEnumerable enumerable)
    {
        var builder = new StringBuilder("[");
        var count = 0;

        foreach (var item in enumerable)
        {
            if (count > 0)
            {
                builder.Append(", ");
            }

            if (count >= EnumerablePreviewCount)
            {
                builder.Append('…');
                break;
            }

            builder.Append(FormatValue(item));
            count++;
        }

        builder.Append(']');
        return builder.ToString();
    }

    private static ValueStackFrame? CreateCallSiteFrame()
    {
        try
        {
            var trace = new StackTrace(1, true);
            foreach (var frame in trace.GetFrames() ?? Array.Empty<StackFrame>())
            {
                var method = frame.GetMethod();
                if (method is null)
                {
                    continue;
                }

                if (IsInternalMethod(method))
                {
                    continue;
                }

                var signature = BuildSignature(method, Array.Empty<ArgumentCapture>());
                var source = BuildSourceInfoForCallSite(method, frame);
                return new ValueStackFrame(signature, Array.Empty<ArgumentCapture>(), source);
            }
        }
        catch
        {
            // ignore and fallback to recorded frames only
        }

        return null;
    }

    private static bool IsInternalMethod(MethodBase method)
    {
        var declaringType = method.DeclaringType;
        if (declaringType is null)
        {
            return false;
        }

        var ns = declaringType.Namespace;
        if (string.IsNullOrEmpty(ns))
        {
            return false;
        }

        return ns.StartsWith("StackValueTracer", StringComparison.Ordinal);
    }

    private static SourceInfo? BuildSourceInfoForCallSite(MethodBase method, StackFrame frame)
    {
        var snippet = CSharpSourceProvider.TryGetSnippet(method, SnippetContextLines, focusText: "ValueStack.Dump");
        if (snippet is { Count: > 0 })
        {
            var primary = snippet.FirstOrDefault(line => line.IsPrimary);
            var highlightLine = primary.LineNumber != 0 ? primary.LineNumber : snippet[0].LineNumber;
            var label = method.Module.Assembly.GetName().Name ?? method.Module.Name ?? method.DeclaringType?.Name ?? "<assembly>";
            return new SourceInfo(label, highlightLine, frame.GetFileColumnNumber(), snippet);
        }

        var fallback = SourceInfoProvider.TryGetSourceInfo(method, SnippetContextLines);
        if (fallback is null)
        {
            return null;
        }

        var preferredLine = frame.GetFileLineNumber();
        var retargeted = RetargetSnippetHighlight(fallback.Snippet, preferredLine);
        var primaryLine = retargeted.FirstOrDefault(line => line.IsPrimary).LineNumber;
        var line = primaryLine != 0 ? primaryLine : fallback.Line;
        return new SourceInfo(fallback.FilePath, line, frame.GetFileColumnNumber(), retargeted);
    }

    private static IReadOnlyList<SnippetLine> RetargetSnippetHighlight(IReadOnlyList<SnippetLine> original, int preferredLine)
    {
        if (original.Count == 0)
        {
            return original;
        }

        var targetIndex = -1;
        if (preferredLine > 0)
        {
            for (var i = 0; i < original.Count; i++)
            {
                if (original[i].LineNumber == preferredLine)
                {
                    targetIndex = i;
                    break;
                }
            }
        }

        if (targetIndex < 0)
        {
            targetIndex = FindLineContaining(original, "ValueStack.Dump");
        }

        if (targetIndex < 0)
        {
            targetIndex = original.Count - 1;
        }

        if (targetIndex < 0)
        {
            return original;
        }

        var adjusted = new List<SnippetLine>(original.Count);
        for (var i = 0; i < original.Count; i++)
        {
            var line = original[i];
            adjusted.Add(new SnippetLine(line.LineNumber, line.Text, i == targetIndex));
        }

        return adjusted;
    }

    private static int FindLineContaining(IReadOnlyList<SnippetLine> lines, string value)
    {
        for (var i = 0; i < lines.Count; i++)
        {
            if (lines[i].Text.Contains(value, StringComparison.Ordinal))
            {
                return i;
            }
        }

        return -1;
    }

    private sealed class StackContext
    {
        public Stack<ValueStackFrame> Frames { get; } = new();
        public ValueStackFrame[]? Snapshot { get; set; }
    }

    private readonly record struct ArgumentCapture(string Name, string Display);

    private sealed class ValueStackFrame
    {
        public ValueStackFrame(string signature, IReadOnlyList<ArgumentCapture> arguments, SourceInfo? source)
        {
            Signature = signature;
            Arguments = arguments;
            Source = source;
        }

        public string Signature { get; }
        public IReadOnlyList<ArgumentCapture> Arguments { get; }
        public SourceInfo? Source { get; }
    }
}

internal sealed class ValueStackScope : IDisposable
{
    private bool _disposed;

    internal ValueStackScope(bool isActive)
    {
        IsActive = isActive;
    }

    internal bool IsActive { get; }

    internal bool IsDeferred { get; private set; }

    public void Dispose()
    {
        if (_disposed || !IsActive)
        {
            return;
        }

        _disposed = true;
        ValueStack.Pop();
    }

    internal void Defer()
    {
        if (IsActive)
        {
            IsDeferred = true;
        }
    }
}
