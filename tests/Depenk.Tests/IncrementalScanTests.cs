using Depenk.Analysis;
using Depenk.Core;

namespace Depenk.Tests;

public class IncrementalScanTests
{
    [Fact]
    public void Rescan_ReparsesOnlyChangedFiles_AndPicksUpChanges()
    {
        using var ws = FixtureScanTests.CopyFixture();
        var cache = new ParseCache();
        var orchestrator = new ScanOrchestrator(cache);

        var first = orchestrator.Scan(ws.Root);
        var parsesAfterFirst = cache.ParseCount;
        Assert.DoesNotContain(first.Endpoints, e => e.Route == "/api/orders/{id}/cancel");

        ws.File("orders/src/Orders.Api/CancelController.cs", """
            using Microsoft.AspNetCore.Mvc;
            namespace Acme.Orders.Api;
            [Route("api/orders")]
            public class CancelController : ControllerBase { [HttpPost("{id}/cancel")] public Task Cancel(Guid id) => Task.CompletedTask; }
            """);
        var second = orchestrator.Scan(ws.Root);

        Assert.Equal(1, cache.ParseCount - parsesAfterFirst);
        Assert.Contains(second.Endpoints, e => e.Route == "/api/orders/{id}/cancel");
    }

    [Fact]
    public void Manifest_DetectsChanges()
    {
        using var ws = FixtureScanTests.CopyFixture();
        Assert.False(WorkspaceManifest.IsUpToDate(ws.Root));

        GraphJson.Save(new ScanOrchestrator().Scan(ws.Root), ScanOrchestrator.GraphPath(ws.Root));
        WorkspaceManifest.Save(ws.Root, WorkspaceManifest.Compute(ws.Root));
        Assert.True(WorkspaceManifest.IsUpToDate(ws.Root));

        ws.File("orders/src/Orders.Client/Models.cs", "namespace Acme.Orders.Client; public class Changed {}");
        Assert.False(WorkspaceManifest.IsUpToDate(ws.Root));
    }

    [Fact]
    public void Manifest_KeysArePortable()
    {
        using var ws = FixtureScanTests.CopyFixture();
        var m = WorkspaceManifest.Compute(ws.Root);
        Assert.Contains("repo:orders", m.Keys);
        Assert.Contains("orders/src/Orders.Api/OrdersController.cs", m.Keys);
        Assert.Contains("depenk.yml", m.Keys);
        Assert.All(m.Keys, k => Assert.DoesNotContain((char)92, k));
    }
}
