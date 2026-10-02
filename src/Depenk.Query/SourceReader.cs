using System.Text.RegularExpressions;
using Depenk.Core.Model;

namespace Depenk.Query;

public sealed class SourceReader(string workspace)
{
    // TrimEndingDirectorySeparator keeps a root's separator ("C:\", "/"): trimming it would make Combine drive-relative
    private readonly string _root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(workspace));
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

        // Rule d: Size cap and streaming read that keeps only the lines it returns
        var ctx = Math.Clamp(context, 0, 50);
        var (start, numbered) = ReadWindow(full, Math.Max(1, location.Line), ctx, location.Path);
        return new SourceSnippet(nodeId, location.Path, location.Line, start, numbered);
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

    /// <summary>
    /// Returns the numbered lines target±ctx (target clamped to the last line), holding at most 2·ctx+1 lines in memory.
    /// Zero-length entries are never opened: FIFOs and devices report size 0 and would block or never end.
    /// </summary>
    private static (int Start, List<string> Lines) ReadWindow(string fullPath, int target, int ctx, string relativePath)
    {
        try
        {
            var length = new FileInfo(fullPath).Length;
            if (length > MaxFileSize)
                throw new QueryException(QueryException.InvalidArgument, $"file too large to snippet",
                    "Files larger than 5 MB cannot be read as snippets.");
            if (length == 0) return (1, []);

            var window = new Queue<string>(2 * ctx + 1);
            var stopAfter = (long)target + ctx;
            long count = 0;
            using (var reader = new StreamReader(fullPath))
            {
                while (count < stopAfter && reader.ReadLine() is { } text)
                {
                    count++;
                    if (window.Count == 2 * ctx + 1) window.Dequeue();
                    window.Enqueue(text);
                }
            }

            var line = Math.Min(target, count);
            var start = Math.Max(1, line - ctx);
            var end = Math.Min(count, line + ctx);
            var firstInWindow = count - window.Count + 1;
            var numbered = window.Skip((int)(start - firstInWindow)).Take((int)Math.Max(0, end - start + 1))
                .Select((text, i) => $"{start + i,5}| {TruncateLine(text)}")
                .ToList();
            return ((int)start, numbered);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            throw FileException(relativePath, ex, missing: ex is FileNotFoundException or DirectoryNotFoundException);
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
        return new QueryException(code, message,
            missing ? "Run rescan to refresh the graph." : "The file may be locked by another process or unreadable; try again.");
    }
}
