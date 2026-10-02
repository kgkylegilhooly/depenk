using Depenk.Analysis;
using Depenk.Mcp;

namespace Depenk.Tests.Mcp;

public class GraphStoreTests
{
    private const string NewController = """
        using Microsoft.AspNetCore.Mvc;
        namespace Acme.Orders.Api;
        [Route("api/orders")]
        public class CancelController : ControllerBase { [HttpPost("{id}/cancel")] public Task Cancel(Guid id) => Task.CompletedTask; }
        """;

    [Fact]
    public void NoGraph_ScansSynchronouslyOnFirstUse()
    {
        using var ws = FixtureScanTests.CopyFixture();
        var store = new GraphStore(ws.Root);

        var s = store.Current();

        Assert.Equal(5, s.Graph.Repos.Count);
        Assert.False(s.Stale);
        Assert.Equal(1, store.ScanCount);
        Assert.True(File.Exists(ScanOrchestrator.GraphPath(ws.Root)));
        Assert.True(File.Exists(WorkspaceManifest.ManifestPath(ws.Root)));
        Assert.Same(s.Graph, store.Current().Graph); // cached
    }

    [Fact]
    public void UpToDateGraph_IsLoadedWithoutScanning()
    {
        using var ws = FixtureScanTests.CopyFixture();
        new GraphStore(ws.Root).Current();

        var store = new GraphStore(ws.Root);
        var s = store.Current();

        Assert.Equal((0, false, 5), (store.ScanCount, s.Stale, s.Graph.Repos.Count));
        Assert.Null(store.StartBackgroundRefresh());
    }

    [Fact]
    public async Task StaleGraph_IsServedWhileRefreshing_ThenSwapped()
    {
        using var ws = FixtureScanTests.CopyFixture();
        new GraphStore(ws.Root).Current();
        ws.File("orders/src/Orders.Api/CancelController.cs", NewController);

        var store = new GraphStore(ws.Root);
        var before = store.Current();
        Assert.True(before.Stale);
        Assert.DoesNotContain(before.Graph.Endpoints, e => e.Route == "/api/orders/{id}/cancel");

        var refresh = store.StartBackgroundRefresh();
        Assert.NotNull(refresh);
        Parallel.For(0, 200, _ =>
        {
            var snap = store.Current();
            Assert.Same(snap.Graph, snap.Index.Graph); // never a mixed snapshot
            Assert.True(snap.Graph.Endpoints.Count >= before.Graph.Endpoints.Count);
        });
        await refresh!;

        var after = store.Current();
        Assert.False(after.Stale);
        Assert.Contains(after.Graph.Endpoints, e => e.Route == "/api/orders/{id}/cancel");
        Assert.Equal(1, store.ScanCount);
        Assert.True(WorkspaceManifest.IsUpToDate(ws.Root));
    }

    [Fact]
    public void Rescan_PicksUpChangesSynchronously()
    {
        using var ws = FixtureScanTests.CopyFixture();
        var store = new GraphStore(ws.Root);
        store.Current();
        ws.File("orders/src/Orders.Api/CancelController.cs", NewController);

        var s = store.Rescan();

        Assert.Contains(s.Graph.Endpoints, e => e.Route == "/api/orders/{id}/cancel");
        Assert.Same(s.Graph, store.Current().Graph);
        Assert.Equal(2, store.ScanCount);
    }

    [Fact]
    public void CorruptGraphFile_TriggersAScan()
    {
        using var ws = FixtureScanTests.CopyFixture();
        ws.File(".depenk/graph.json", "{ not json");
        var store = new GraphStore(ws.Root);
        Assert.Equal(5, store.Current().Graph.Repos.Count);
        Assert.Equal(1, store.ScanCount);
    }
}
