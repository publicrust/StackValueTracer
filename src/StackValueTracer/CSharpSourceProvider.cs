using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Text;
using ICSharpCode.Decompiler;
using ICSharpCode.Decompiler.CSharp;
using ICSharpCode.Decompiler.Metadata;

namespace StackValueTracer;

internal static class CSharpSourceProvider
{
    private static readonly ConcurrentDictionary<string, CSharpDecompiler> DecompilerCache = new(StringComparer.OrdinalIgnoreCase);

    public static IReadOnlyList<SnippetLine>? TryGetSnippet(MethodBase method, int contextLines, string? focusText = null)
    {
        var location = method.Module.Assembly.Location;
        if (string.IsNullOrWhiteSpace(location) || !File.Exists(location))
        {
            return null;
        }

        try
        {
            var decompiler = DecompilerCache.GetOrAdd(location, static path => CreateDecompiler(path));
            var entityHandle = MetadataTokens.EntityHandle(method.MetadataToken);
            if (entityHandle.Kind is not (HandleKind.MethodDefinition or HandleKind.MemberReference))
            {
                return null;
            }

            string code;
            if (entityHandle.Kind == HandleKind.MemberReference)
            {
                var resolved = decompiler.TypeSystem.MainModule.ResolveEntity(entityHandle);
                if (resolved is null)
                {
                    return null;
                }

                code = decompiler.DecompileAsString(resolved.MetadataToken);
            }
            else
            {
                code = decompiler.DecompileAsString(entityHandle);
            }

            if (string.IsNullOrWhiteSpace(code))
            {
                return null;
            }

            var lines = SplitLines(code);
            if (lines.Length == 0)
            {
                return null;
            }

            var primaryIndex = DeterminePrimaryLineIndex(method, lines, focusText);
            if (primaryIndex < 0)
            {
                primaryIndex = 0;
            }

            return BuildSnippet(lines, primaryIndex, contextLines);
        }
        catch
        {
            return null;
        }
    }

    private static CSharpDecompiler CreateDecompiler(string path)
    {
        var settings = new DecompilerSettings
        {
            UsingDeclarations = false,
            ExpandMemberDefinitions = false,
            ExpressionTrees = false,
            ShowXmlDocumentation = false,
            LoadInMemory = false
        };

        return new CSharpDecompiler(path, settings);
    }

    private static string[] SplitLines(string code)
    {
        var normalized = code.Replace("\r\n", "\n").Replace('\r', '\n');
        var parts = normalized.Split('\n');
        for (var i = 0; i < parts.Length; i++)
        {
            parts[i] = parts[i].Replace('\t', ' ');
        }

        return parts;
    }

    private static int DeterminePrimaryLineIndex(MethodBase method, IReadOnlyList<string> lines, string? focusText)
    {
        if (!string.IsNullOrWhiteSpace(focusText))
        {
            var focusIndex = FindLineContaining(lines, focusText);
            if (focusIndex >= 0)
            {
                return focusIndex;
            }
        }

        var nameCandidates = GetMethodNameCandidates(method);
        for (var i = 0; i < lines.Count; i++)
        {
            var line = lines[i];
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            foreach (var candidate in nameCandidates)
            {
                if (line.Contains(candidate, StringComparison.Ordinal))
                {
                    var bodyIndex = FindFirstContentLine(lines, i + 1);
                    return bodyIndex >= 0 ? bodyIndex : i;
                }
            }
        }

        return -1;
    }

    private static IEnumerable<string> GetMethodNameCandidates(MethodBase method)
    {
        if (method.IsConstructor)
        {
            yield return method.DeclaringType?.Name ?? method.Name;
            yield break;
        }

        yield return method.Name;

        if (method.Name.StartsWith("get_", StringComparison.Ordinal) || method.Name.StartsWith("set_", StringComparison.Ordinal))
        {
            yield return method.Name[4..];
        }
    }

    private static int FindFirstContentLine(IReadOnlyList<string> lines, int startIndex)
    {
        for (var i = startIndex; i < lines.Count; i++)
        {
            var line = lines[i];
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            var trimmed = line.TrimStart();
            if (trimmed.StartsWith("//", StringComparison.Ordinal) || trimmed is "{" or "}")
            {
                continue;
            }

            return i;
        }

        return -1;
    }

    private static IReadOnlyList<SnippetLine> BuildSnippet(string[] lines, int primaryIndex, int contextLines)
    {
        var start = Math.Max(0, primaryIndex - contextLines);
        var end = Math.Min(lines.Length - 1, primaryIndex + contextLines);
        var result = new List<SnippetLine>(end - start + 1);

        for (var i = start; i <= end; i++)
        {
            result.Add(new SnippetLine(i + 1, lines[i], i == primaryIndex));
        }

        return result;
    }

    private static int FindLineContaining(IReadOnlyList<string> lines, string value)
    {
        for (var i = 0; i < lines.Count; i++)
        {
            if (lines[i].Contains(value, StringComparison.Ordinal))
            {
                return i;
            }
        }

        return -1;
    }
}
