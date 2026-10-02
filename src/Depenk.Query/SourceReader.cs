using Depenk.Core.Model;

namespace Depenk.Query;

public sealed class SourceReader(string workspace)
{
    private readonly string _root = Path.GetFullPath(workspace).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    public SourceSnippet Read(GraphIndex index, string nodeId, int context = 10)
    {
        index.Get(nodeId); // not_found with suggestions
        var location = LocationOf(index, nodeId)
                       ?? throw new QueryException(QueryException.InvalidArgument, $"{nodeId} has no source location",
                           "get_source works for endpoints, client methods, call sites, models and projects.");
        var full = Path.GetFullPath(Path.Combine(_root, location.Path.Replace('/', Path.DirectorySeparatorChar)));
        if (!IsUnderRoot(full) || !LinksStayInside(full))
            throw new QueryException(QueryException.OutsideWorkspace,
                $"Refusing to read '{location.Path}': it resolves outside the workspace", "The graph may be stale or tampered with; run rescan.");
        if (!File.Exists(full))
            throw new QueryException(QueryException.NotFound, $"Source file no longer exists: {location.Path}", "Run rescan to refresh the graph.");

        var lines = File.ReadAllLines(full);
        var ctx = Math.Clamp(context, 0, 50);
        var line = Math.Clamp(location.Line, 1, Math.Max(1, lines.Length));
        var start = Math.Max(1, line - ctx);
        var end = Math.Min(lines.Length, line + ctx);
        var numbered = Enumerable.Range(start, Math.Max(0, end - start + 1)).Select(n => $"{n,5}| {lines[n - 1]}").ToList();
        return new SourceSnippet(nodeId, location.Path, location.Line, start, numbered);
    }

    private static SourceLocation? LocationOf(GraphIndex ix, string id) =>
        ix.Endpoints.GetValueOrDefault(id)?.Location
        ?? ix.ClientMethods.GetValueOrDefault(id)?.Location
        ?? ix.CallSites.GetValueOrDefault(id)?.Location
        ?? ix.Models.GetValueOrDefault(id)?.Location
        ?? (ix.Projects.GetValueOrDefault(id) is { } p ? new SourceLocation(p.Path, 1) : null);

    private bool IsUnderRoot(string full) =>
        full.Equals(_root, StringComparison.OrdinalIgnoreCase)
        || full.StartsWith(_root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The file and every existing directory below the root must not be a link resolving outside the root.
    /// The root itself may be a link (e.g. ~/code symlinked elsewhere) — it is the trust anchor.
    /// </summary>
    private bool LinksStayInside(string full)
    {
        for (var p = full; p is not null && p.Length > _root.Length && IsUnderRoot(p); p = Path.GetDirectoryName(p))
        {
            FileSystemInfo? fsi = File.Exists(p) ? new FileInfo(p) : Directory.Exists(p) ? new DirectoryInfo(p) : null;
            if (fsi?.LinkTarget is null) continue;
            var target = fsi.ResolveLinkTarget(returnFinalTarget: true)?.FullName;
            if (target is null || !IsUnderRoot(Path.GetFullPath(target))) return false;
        }
        return true;
    }
}
