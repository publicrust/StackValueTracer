using System;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Runtime.CompilerServices;
using System.Text;

namespace StackValueTracer;

internal static class SourceInfoProvider
{
    private static readonly ConcurrentDictionary<MethodBase, SourceInfo?> MethodCache = new();
    private static readonly ConcurrentDictionary<Assembly, ReaderCache?> AssemblyReaders = new();
    private static readonly Guid EmbeddedSourceKind = new("0E8A571B-6926-466E-B4AD-8AB04611F5FE");

    public static SourceInfo? TryGetSourceInfo(MethodBase method, int contextLines)
    {
        return MethodCache.GetOrAdd(method, static (m, ctx) => ComputeSourceInfo(m, ctx), contextLines);
    }

    private static SourceInfo? ComputeSourceInfo(MethodBase method, int contextLines)
    {
        int metadataToken;
        try
        {
            metadataToken = method.MetadataToken;
        }
        catch
        {
            return null;
        }

        ReaderCache? cacheEntry;
        try
        {
            cacheEntry = AssemblyReaders.GetOrAdd(method.Module.Assembly, static asm => LoadReader(asm));
        }
        catch
        {
            return null;
        }

        if (cacheEntry is null)
        {
            return null;
        }

        MetadataReader reader;
        try
        {
            reader = cacheEntry.ReaderProvider.GetMetadataReader();
        }
        catch
        {
            return null;
        }

        EntityHandle entityHandle;
        try
        {
            entityHandle = MetadataTokens.EntityHandle(metadataToken);
        }
        catch
        {
            return null;
        }

        if (entityHandle.Kind != HandleKind.MethodDefinition)
        {
            return null;
        }

        var methodHandle = (MethodDefinitionHandle)entityHandle;
        var debugHandle = methodHandle.ToDebugInformationHandle();
        if (debugHandle.IsNil)
        {
            return null;
        }

        MethodDebugInformation debugInfo;
        try
        {
            debugInfo = reader.GetMethodDebugInformation(debugHandle);
        }
        catch
        {
            return null;
        }

        foreach (var sequencePoint in debugInfo.GetSequencePoints())
        {
            if (sequencePoint.IsHidden || sequencePoint.StartLine == SequencePoint.HiddenLine)
            {
                continue;
            }

            if (sequencePoint.Document.IsNil)
            {
                continue;
            }

            var document = reader.GetDocument(sequencePoint.Document);
            var path = GetDocumentPath(reader, document);
            if (string.IsNullOrWhiteSpace(path))
            {
                continue;
            }

            var lines = GetSourceLines(reader, sequencePoint.Document);
            if (lines is null)
            {
                continue;
            }

            var lineText = GetLineText(lines, sequencePoint.StartLine);
            if (IsIgnorableLine(lineText))
            {
                continue;
            }

            var snippet = BuildSnippet(lines, sequencePoint.StartLine, contextLines);
            return new SourceInfo(path, sequencePoint.StartLine, sequencePoint.StartColumn, snippet);
        }

        var csharpSnippet = CSharpSourceProvider.TryGetSnippet(method, contextLines);
        if (csharpSnippet is { Count: > 0 })
        {
            var assemblyName = method.Module.Assembly.GetName().Name ?? method.Module.Name ?? "<assembly>";
            return new SourceInfo(assemblyName, csharpSnippet[0].LineNumber, 0, csharpSnippet);
        }

        var asyncAttr = method.GetCustomAttribute<AsyncStateMachineAttribute>();
        if (asyncAttr?.StateMachineType is { } asyncStateMachine)
        {
            var moveNext = asyncStateMachine.GetMethod("MoveNext", BindingFlags.Instance | BindingFlags.NonPublic);
            if (moveNext is not null && !ReferenceEquals(moveNext, method))
            {
                return TryGetSourceInfo(moveNext, contextLines);
            }
        }

        var iteratorAttr = method.GetCustomAttribute<IteratorStateMachineAttribute>();
        if (iteratorAttr?.StateMachineType is { } iteratorStateMachine)
        {
            var moveNext = iteratorStateMachine.GetMethod("MoveNext", BindingFlags.Instance | BindingFlags.NonPublic);
            if (moveNext is not null && !ReferenceEquals(moveNext, method))
            {
                return TryGetSourceInfo(moveNext, contextLines);
            }
        }

        var ilSnippet = IlDisassembler.Disassemble(method);
        if (ilSnippet.Count > 0)
        {
            var primary = ilSnippet.FirstOrDefault(line => line.IsPrimary);
            var lineNumber = primary.LineNumber == 0 ? 1 : primary.LineNumber;
            return new SourceInfo(method.Module.Name ?? method.DeclaringType?.Name ?? "<IL>", lineNumber, column: 0, ilSnippet);
        }

        return null;
    }

    private static ReaderCache? LoadReader(Assembly assembly)
    {
        try
        {
            var location = assembly.Location;
            if (!string.IsNullOrWhiteSpace(location) && File.Exists(location))
            {
                using var assemblyStream = File.OpenRead(location);
                using var peReader = new PEReader(assemblyStream);

                foreach (var entry in peReader.ReadDebugDirectory())
                {
                    switch (entry.Type)
                    {
                        case DebugDirectoryEntryType.EmbeddedPortablePdb:
                            var provider = peReader.ReadEmbeddedPortablePdbDebugDirectoryData(entry);
                            return new ReaderCache(provider);
                        case DebugDirectoryEntryType.CodeView:
                            var codeView = peReader.ReadCodeViewDebugDirectoryData(entry);
                            if (!string.IsNullOrEmpty(codeView.Path) && File.Exists(codeView.Path))
                            {
                                var providerFromFile = LoadFromFile(codeView.Path);
                                if (providerFromFile is not null)
                                {
                                    return providerFromFile;
                                }
                            }
                            break;
                    }
                }
            }

            var pdbPath = TryResolvePortablePdbPath(assembly);
            if (pdbPath is not null)
            {
                return LoadFromFile(pdbPath);
            }
        }
        catch
        {
            return null;
        }

        return null;
    }

    private static ReaderCache? LoadFromFile(string path)
    {
        try
        {
            var bytes = File.ReadAllBytes(path);
            var provider = MetadataReaderProvider.FromPortablePdbImage(ImmutableArray.Create(bytes));
            return new ReaderCache(provider);
        }
        catch
        {
            return null;
        }
    }

    private static string? TryResolvePortablePdbPath(Assembly assembly)
    {
        if (string.IsNullOrWhiteSpace(assembly.Location))
        {
            return null;
        }

        var pdbPath = Path.ChangeExtension(assembly.Location, ".pdb");
        return File.Exists(pdbPath) ? pdbPath : null;
    }

    private static string[]? GetSourceLines(MetadataReader reader, DocumentHandle documentHandle)
    {
        foreach (var infoHandle in reader.GetCustomDebugInformation(documentHandle))
        {
            var info = reader.GetCustomDebugInformation(infoHandle);
            if (reader.GetGuid(info.Kind) != EmbeddedSourceKind)
            {
                continue;
            }

            try
            {
                var blob = reader.GetBlobBytes(info.Value);
                if (blob.Length < sizeof(int))
                {
                    continue;
                }

                var uncompressedLength = BinaryPrimitives.ReadInt32LittleEndian(blob);
                if (uncompressedLength == 0)
                {
                    return SplitLines(Encoding.UTF8.GetString(blob, sizeof(int), blob.Length - sizeof(int)));
                }

                using var dataStream = new MemoryStream(blob, sizeof(int), blob.Length - sizeof(int));
                using var deflate = new DeflateStream(dataStream, CompressionMode.Decompress, leaveOpen: false);
                var buffer = new byte[uncompressedLength];
                var total = 0;
                while (total < uncompressedLength)
                {
                    var read = deflate.Read(buffer, total, uncompressedLength - total);
                    if (read == 0)
                    {
                        break;
                    }

                    total += read;
                }

                return SplitLines(Encoding.UTF8.GetString(buffer, 0, total));
            }
            catch
            {
                return null;
            }
        }

        return null;
    }

    private static string[] SplitLines(string contents)
    {
        var normalized = contents.Replace("\r\n", "\n").Replace('\r', '\n');
        return normalized.Split('\n');
    }

    private static string? GetLineText(string[] lines, int lineNumber)
    {
        var index = lineNumber - 1;
        if (index < 0 || index >= lines.Length)
        {
            return null;
        }

        return lines[index];
    }

    private static bool IsIgnorableLine(string? lineText)
    {
        if (lineText is null)
        {
            return true;
        }

        var trimmed = lineText.Trim();
        if (trimmed.Length == 0)
        {
            return true;
        }

        return trimmed is "{" or "}";
    }

    private static IReadOnlyList<SnippetLine>? BuildSnippet(string[] lines, int line, int contextLines)
    {
        try
        {
            if (lines.Length == 0)
            {
                return null;
            }

            var start = Math.Max(1, line - contextLines);
            var end = Math.Min(lines.Length, line + contextLines);
            var snippet = new List<SnippetLine>(end - start + 1);

            for (var current = start; current <= end; current++)
            {
                var text = lines[current - 1];
                snippet.Add(new SnippetLine(current, text, current == line));
            }

            return snippet;
        }
        catch
        {
            return null;
        }
    }

    private static string? GetDocumentPath(MetadataReader reader, Document document)
    {
        try
        {
            if (document.Name.IsNil)
            {
                return null;
            }

            var name = reader.GetString(document.Name);
            if (string.IsNullOrEmpty(name))
            {
                return null;
            }

            return name;
        }
        catch
        {
            return null;
        }
    }

    private sealed class ReaderCache
    {
        public ReaderCache(MetadataReaderProvider readerProvider)
        {
            ReaderProvider = readerProvider;
        }

        public MetadataReaderProvider ReaderProvider { get; }
    }
}

internal sealed class SourceInfo
{
    public SourceInfo(string filePath, int line, int column, IReadOnlyList<SnippetLine>? snippet)
    {
        FilePath = filePath;
        Line = line;
        Column = column;
        Snippet = snippet ?? Array.Empty<SnippetLine>();
    }

    public string FilePath { get; }
    public int Line { get; }
    public int Column { get; }
    public IReadOnlyList<SnippetLine> Snippet { get; }
}

internal readonly record struct SnippetLine(int LineNumber, string Text, bool IsPrimary);
