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
    public void Manifest_MixedCaseNames_AreUpToDateAfterSave()
    {
        using var ws = FixtureScanTests.CopyFixture();
        ws.File("orders/src/Orders.Api/Zebra.cs", "namespace Z; public class Zebra {}");
        ws.File("orders/src/Orders.Api/apple.cs", "namespace Z; public class Apple {}");
        GraphJson.Save(new ScanOrchestrator().Scan(ws.Root), ScanOrchestrator.GraphPath(ws.Root));
        WorkspaceManifest.Save(ws.Root, WorkspaceManifest.Compute(ws.Root));
        Assert.True(WorkspaceManifest.IsUpToDate(ws.Root));
    }

    [Fact]
    public void Manifest_UnreadableGraphOrManifest_IsNotUpToDate()
    {
        using var ws = FixtureScanTests.CopyFixture();
        GraphJson.Save(new ScanOrchestrator().Scan(ws.Root), ScanOrchestrator.GraphPath(ws.Root));
        WorkspaceManifest.Save(ws.Root, WorkspaceManifest.Compute(ws.Root));
        using var locked = new FileStream(WorkspaceManifest.ManifestPath(ws.Root), FileMode.Open, FileAccess.Read, FileShare.None);
        Assert.False(WorkspaceManifest.IsUpToDate(ws.Root));
    }

    [Fact]
    public void Rescan_ReanalyzesOnlyChangedProject()
    {
        using var ws = FixtureScanTests.CopyFixture();
        var orchestrator = new ScanOrchestrator(new ParseCache());
        orchestrator.Scan(ws.Root);
        var projects = orchestrator.AnalyzedProjectCount;
        Assert.True(projects > 1);

        orchestrator.Scan(ws.Root);
        Assert.Equal(projects, orchestrator.AnalyzedProjectCount);

        ws.File("orders/src/Orders.Api/CancelController.cs", "namespace Acme.Orders.Api; public class CancelController {}");
        orchestrator.Scan(ws.Root);
        Assert.Equal(projects + 1, orchestrator.AnalyzedProjectCount);
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

    [Fact]
    public void Manifest_RecordsToolAndSchemaVersion_AndAnUpgradeInvalidatesIt()
    {
        using var ws = FixtureScanTests.CopyFixture();
        var m = WorkspaceManifest.Compute(ws.Root);
        Assert.Equal(WorkspaceManifest.ToolVersion, m[WorkspaceManifest.VersionKey]);
        Assert.False(string.IsNullOrEmpty(m[WorkspaceManifest.VersionKey]));
        Assert.Equal("1", m[WorkspaceManifest.SchemaVersionKey]);

        GraphJson.Save(new ScanOrchestrator().Scan(ws.Root), ScanOrchestrator.GraphPath(ws.Root));
        WorkspaceManifest.Save(ws.Root, m);
        Assert.True(WorkspaceManifest.IsUpToDate(ws.Root));

        // a manifest written by an older depenk is stale
        var old = new SortedDictionary<string, string>(m, StringComparer.Ordinal) { [WorkspaceManifest.VersionKey] = "0.0.1-old" };
        WorkspaceManifest.Save(ws.Root, old);
        Assert.False(WorkspaceManifest.IsUpToDate(ws.Root));

        var noVersion = new SortedDictionary<string, string>(m, StringComparer.Ordinal);
        noVersion.Remove(WorkspaceManifest.VersionKey);
        noVersion.Remove(WorkspaceManifest.SchemaVersionKey);
        WorkspaceManifest.Save(ws.Root, noVersion); // pre-upgrade manifest format
        Assert.False(WorkspaceManifest.IsUpToDate(ws.Root));
    }
}
