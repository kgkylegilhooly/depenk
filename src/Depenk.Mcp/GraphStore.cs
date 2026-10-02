using System.Text.Json;
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
    private GraphSnapshot? _snapshot;
    private volatile bool _knownStale;
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
                    if (TryLoad(out var cached))
                    {
                        _knownStale = !WorkspaceManifest.IsUpToDate(Workspace);
                        s = Wrap(cached);
                    }
                    else
                    {
                        s = ScanLocked();
                    }
                    Volatile.Write(ref _snapshot, s);
                }
            }
        }
        var stale = _knownStale || Volatile.Read(ref _refreshing) == 1;
        return s.Stale == stale ? s : s with { Stale = stale };
    }

    public Task? StartBackgroundRefresh()
    {
        Current();
        if (!_knownStale || Interlocked.CompareExchange(ref _refreshing, 1, 0) != 0) return null;
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
        _knownStale = false;
        return Wrap(graph);
    }

    private bool TryLoad(out DepGraph graph)
    {
        graph = null!;
        var path = ScanOrchestrator.GraphPath(Workspace);
        if (!File.Exists(path)) return false;
        try
        {
            graph = GraphJson.Load(path);
            return graph.SchemaVersion == 1;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
        {
            return false;
        }
    }

    private static GraphSnapshot Wrap(DepGraph graph)
    {
        var index = new GraphIndex(graph);
        return new GraphSnapshot(graph, index, new QueryService(index), false);
    }
}
