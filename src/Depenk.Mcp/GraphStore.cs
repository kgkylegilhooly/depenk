using Depenk.Analysis;
using Depenk.Core;
using Depenk.Core.Model;
using Depenk.Query;

namespace Depenk.Mcp;

public sealed record GraphSnapshot(DepGraph Graph, GraphIndex Index, QueryService Query, bool Stale);

/// <summary>
/// Owns the workspace graph for a long-lived host (MCP server). Loads the cached graph instantly, scans when there is
/// none, and refreshes stale graphs in the background while continuing to serve the previous snapshot.
/// </summary>
public sealed class GraphStore(string workspace, ScanOrchestrator? orchestrator = null)
{
    private readonly object _scanLock = new();
    private readonly ScanOrchestrator _orchestrator = orchestrator ?? new ScanOrchestrator(new ParseCache());
    private GraphSnapshot? _snapshot; // its Stale flag is the known staleness of exactly that graph
    private int _refreshing;
    private int _scanCount;

    public string Workspace { get; } = Path.GetFullPath(workspace);
    public int ScanCount => Volatile.Read(ref _scanCount);

    public GraphSnapshot Current()
    {
        var s = Volatile.Read(ref _snapshot);
        if (s is null)
        {
            lock (_scanLock)
            {
                s = _snapshot;
                if (s is null)
                {
                    s = TryLoad() is { } cached
                        ? cached with { Stale = !WorkspaceManifest.IsUpToDate(Workspace) }
                        : ScanLocked();
                    Volatile.Write(ref _snapshot, s);
                }
            }
        }
        return !s.Stale && Volatile.Read(ref _refreshing) == 1 ? s with { Stale = true } : s;
    }

    public Task? StartBackgroundRefresh()
    {
        if (!Current().Stale || Interlocked.CompareExchange(ref _refreshing, 1, 0) != 0) return null;
        return Task.Run(() =>
        {
            try
            {
                lock (_scanLock) Volatile.Write(ref _snapshot, ScanLocked());
            }
            finally
            {
                Volatile.Write(ref _refreshing, 0);
            }
        });
    }

    public GraphSnapshot Rescan()
    {
        lock (_scanLock)
        {
            var s = ScanLocked();
            Volatile.Write(ref _snapshot, s);
            return s;
        }
    }

    /// <summary>Caller must hold <see cref="_scanLock"/>.</summary>
    private GraphSnapshot ScanLocked()
    {
        var manifest = WorkspaceManifest.Compute(Workspace); // before the scan: edits during it stay detectable
        var graph = _orchestrator.Scan(Workspace);
        GraphJson.Save(graph, ScanOrchestrator.GraphPath(Workspace));
        WorkspaceManifest.Save(Workspace, manifest);
        Interlocked.Increment(ref _scanCount);
        return Wrap(graph);
    }

    /// <summary>The cached graph, or null when it is missing, unreadable, from another schema or fails to index.</summary>
    private GraphSnapshot? TryLoad()
    {
        var path = ScanOrchestrator.GraphPath(Workspace);
        if (!File.Exists(path)) return null;
        try
        {
            var graph = GraphJson.Load(path);
            return graph.SchemaVersion == 1 ? Wrap(graph) : null;
        }
        catch (Exception e) when (e is not OperationCanceledException) // a cache we can't use is rebuilt, never fatal
        {
            return null;
        }
    }

    private static GraphSnapshot Wrap(DepGraph graph)
    {
        var index = new GraphIndex(graph);
        return new GraphSnapshot(graph, index, new QueryService(index), false);
    }
}
