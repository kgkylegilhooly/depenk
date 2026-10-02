using Depenk.Core.Model;
using Depenk.Query;
using Depenk.Tests.TestUtil;

namespace Depenk.Tests.Query;

public class QueryAnalyzeRobustnessTests
{
    private const string OrderDto = "model:Orders.Client:Acme.Orders.Client.OrderDto";

    private static EndpointNode Ep(string id) =>
        new(id, "r", "proj:r/P", "GET", id, id, "H", [], [], new SourceLocation("a.cs", 1));

    [Fact]
    public void Overview_Hotspots_ConsiderAllEndpoints()
    {
        var g = new DepGraph();
        g.Repos.Add(new RepoNode("repo:r", "r", "p", null, false));
        for (var i = 0; i < 600; i++) g.Endpoints.Add(Ep($"ep:r:GET:/e{i:D3}"));
        g.ClientMethods.Add(new ClientMethodNode("cm:x", "r", "proj:r/P", "C", "M", "M()", "GET", "/z", "/z",
            "s", Confidence.High, new SourceLocation("a.cs", 1)));
        g.Edges.Add(new Edge(EdgeKind.Targets, "cm:x", "ep:r:GET:/e599", Confidence.High));
        var md = new QueryService(new GraphIndex(g)).Overview();
        Assert.Contains("/e599", md);
    }

    [Fact]
    public void Impact_IsCapped_WithTotals()
    {
        var q = FixtureGraph.Query;
        var r = q.ImpactOfChange("OrderDto", limit: 2);
        Assert.Equal(2, r.ClientMethods.Count);
        Assert.Equal(6, r.Totals.ClientMethods);
        Assert.True(r.Truncated);
        Assert.False(q.ImpactOfChange("OrderDto").Truncated);
    }

    [Fact]
    public void DanglingEdges_DoNotThrow()
    {
        var g = new DepGraph();
        g.Models.Add(new ModelNode("model:r:A", "r", null, "A", ModelKind.Class,
            [new ModelField("G", "Ghost", false, false)], null, null));
        g.Edges.Add(new Edge(EdgeKind.FieldOf, "model:r:A", "model:?:Ghost", Confidence.High) { FieldName = "G" });
        g.Edges.Add(new Edge(EdgeKind.Returns, "ep:unknown", "model:r:A", Confidence.High));
        g.Edges.Add(new Edge(EdgeKind.FieldOf, "model:unknown", "model:r:A", Confidence.High));
        var q = new QueryService(new GraphIndex(g));
        Assert.Single(q.GetModel("A").Fields);
        Assert.Empty(q.FindModelUsages("A").Endpoints);
        Assert.Empty(q.ImpactOfChange("A").Endpoints);
    }

    [Fact]
    public void Trace_ValidatesDirectionBeforeResolvingRoot() =>
        Assert.Equal("invalid_argument",
            Assert.Throws<QueryException>(() => FixtureGraph.Query.Trace("zzz-unknown", "sideways")).Code);
}
