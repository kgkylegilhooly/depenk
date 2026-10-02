using System.Text.RegularExpressions;
using Depenk.Core.Model;

namespace Depenk.Query;

public sealed class SourceReader(string workspace)
{
    private readonly string _root = Path.GetFullPath(workspace).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    private const long MaxFileSize = 5 * 1024 * 1024; // 5 MB
    private const int MaxLineLength = 1999;

    public SourceSnippet Read(GraphIndex index, string nodeId, int context = 10)
    {
        index.Get(nodeId); // not_found with suggestions
        var location = LocationOf(index, nodeId)
                       ?? throw new QueryException(QueryException.InvalidArgument, $"{nodeId} has no source location",
                           "get_source works for endpoints, client methods, call sites, models and projects.");

        // Rule a: Syntactic validation first (no filesystem access)
        ValidatePath(location.Path);

        // Rule b: Extension allow-list
        ValidateExtension(location.Path);

        // Rule c: No-follow link check (top-down)
        var full = VerifyNoFollowPath(location.Path);

        try
        {
            // Rule d: Size cap and streaming read
            var lines = ReadLinesWithCap(full, location.Line, context, location.Path);
            var ctx = Math.Clamp(context, 0, 50);
            var line = Math.Clamp(location.Line, 1, Math.Max(1, lines.Count));
            var start = Math.Max(1, line - ctx);
            var end = Math.Min(lines.Count, line + ctx);
            var numbered = Enumerable.Range(start, Math.Max(0, end - start + 1))
                .Select(n => $"{n,5}| {TruncateLine(lines[n - 1])}")
                .ToList();
            return new SourceSnippet(nodeId, location.Path, location.Line, start, numbered);
        }
        catch (PathTooLongException ex)
        {
            throw FileException(location.Path, ex, missing: false);
        }
        catch (UnauthorizedAccessException ex)
        {
            throw FileException(location.Path, ex, missing: false);
        }
        catch (NotSupportedException ex)
        {
            throw FileException(location.Path, ex, missing: false);
        }
        catch (ArgumentException ex)
        {
            throw FileException(location.Path, ex, missing: false);
        }
        catch (IOException ex)
        {
            throw FileException(location.Path, ex, missing: true);
        }
    }

    private static SourceLocation? LocationOf(GraphIndex ix, string id) =>
        ix.Endpoints.GetValueOrDefault(id)?.Location
        ?? ix.ClientMethods.GetValueOrDefault(id)?.Location
        ?? ix.CallSites.GetValueOrDefault(id)?.Location
        ?? ix.Models.GetValueOrDefault(id)?.Location
        ?? (ix.Projects.GetValueOrDefault(id) is { } p ? new SourceLocation(p.Path, 1) : null);

    private static void ValidatePath(string relativePath)
    {
        if (string.IsNullOrEmpty(relativePath))
            throw new QueryException(QueryException.OutsideWorkspace, "Path is empty", null);

        // Check for rooted paths (leading '/' or '\\')
        if (Path.IsPathRooted(relativePath))
            throw new QueryException(QueryException.OutsideWorkspace, $"Refusing to read '{relativePath}': rooted paths are not allowed", null);

        // Check for ':' (drive letters, alternate data streams)
        if (relativePath.Contains(':'))
            throw new QueryException(QueryException.OutsideWorkspace, $"Refusing to read '{relativePath}': drive letters and alternate data streams not allowed", null);

        // Check for null bytes
        if (relativePath.Contains('\0'))
            throw new QueryException(QueryException.OutsideWorkspace, $"Refusing to read '{relativePath}': null bytes not allowed", null);

        // Split by both '/' and '\\', check for '..' and empty segments
        var segments = Regex.Split(relativePath, @"[\\/]");
        foreach (var segment in segments)
        {
            if (segment == "..")
                throw new QueryException(QueryException.OutsideWorkspace, $"Refusing to read '{relativePath}': '..' segments not allowed", null);
            if (segment == "")
                throw new QueryException(QueryException.OutsideWorkspace, $"Refusing to read '{relativePath}': empty path segments not allowed", null);
        }
    }

    private static void ValidateExtension(string relativePath)
    {
        if (!relativePath.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) &&
            !relativePath.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
            throw new QueryException(QueryException.InvalidArgument, $"{relativePath}: get_source only serves .cs and .csproj files",
                "Other file types are not supported for snippet retrieval.");
    }

    private string VerifyNoFollowPath(string relativePath)
    {
        var current = _root;
        var segments = Regex.Split(relativePath, @"[\\/]");

        // Build path segment by segment, checking for links at each step
        for (int i = 0; i < segments.Length; i++)
        {
            current = Path.Combine(current, segments[i]);

            try
            {
                var attrs = File.GetAttributes(current);
                var isLink = (attrs & FileAttributes.ReparsePoint) != 0;
                if (isLink)
                    throw new QueryException(QueryException.OutsideWorkspace,
                        $"Refusing to read '{relativePath}': paths through symlinks or junctions are not followed.", null);

                // Check if it's a directory (but not if it's the final segment)
                var isDirectory = (attrs & FileAttributes.Directory) != 0;
                if (isDirectory && i == segments.Length - 1)
                    throw new QueryException(QueryException.InvalidArgument,
                        $"'{relativePath}' is a directory, not a file.", null);
            }
            catch (FileNotFoundException)
            {
                throw new QueryException(QueryException.NotFound, $"Source file no longer exists: {relativePath}",
                    "Run rescan to refresh the graph.");
            }
            catch (DirectoryNotFoundException)
            {
                throw new QueryException(QueryException.NotFound, $"Source file no longer exists: {relativePath}",
                    "Run rescan to refresh the graph.");
            }
            catch (QueryException)
            {
                throw;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or PathTooLongException)
            {
                throw FileException(relativePath, ex, missing: false);
            }
        }

        return current;
    }

    private static List<string> ReadLinesWithCap(string fullPath, int targetLine, int context, string relativePath)
    {
        try
        {
            var fileInfo = new FileInfo(fullPath);
            if (fileInfo.Length > MaxFileSize)
                throw new QueryException(QueryException.InvalidArgument, $"file too large to snippet",
                    "Files larger than 5 MB cannot be read as snippets.");

            var lines = new List<string>();
            var ctx = Math.Clamp(context, 0, 50);
            var stopAfterLine = Math.Min(targetLine + ctx, targetLine + 1000); // Safety cap

            using (var reader = new StreamReader(fullPath))
            {
                string? line;
                int lineNum = 0;
                while ((line = reader.ReadLine()) is not null)
                {
                    lineNum++;
                    lines.Add(line);
                    if (lineNum >= stopAfterLine)
                        break;
                }
            }

            return lines;
        }
        catch (QueryException)
        {
            throw;
        }
        catch (PathTooLongException ex)
        {
            throw FileException(relativePath, ex, missing: false);
        }
        catch (UnauthorizedAccessException ex)
        {
            throw FileException(relativePath, ex, missing: false);
        }
        catch (NotSupportedException ex)
        {
            throw FileException(relativePath, ex, missing: false);
        }
        catch (ArgumentException ex)
        {
            throw FileException(relativePath, ex, missing: false);
        }
        catch (IOException ex)
        {
            throw FileException(relativePath, ex, missing: true);
        }
    }

    private static string TruncateLine(string line)
    {
        if (line.Length <= MaxLineLength)
            return line;
        return line[..MaxLineLength] + "…";
    }

    private static QueryException FileException(string relativePath, Exception innerEx, bool missing)
    {
        var code = missing ? QueryException.NotFound : QueryException.InvalidArgument;
        var message = missing
            ? $"Source file no longer exists: {relativePath}"
            : $"Cannot read file: {relativePath}";
        return new QueryException(code, message, "Run rescan to refresh the graph.");
    }
}
