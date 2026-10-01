using Depenk.Analysis.Diagnostics;
using Depenk.Core.Model;

namespace Depenk.Tests.Analysis;

public class GraphDiagnosticsTests
{
    private static readonly SourceLocation Loc = new("x.cs", 1);

    [Fact]
    public void VersionDrift_AgainstProducerVersion()
    {
        var g = new DepGraph();
        g.Packages.Add(new PackageNode("pkg:Orders.Client", "Orders.Client", ["proj:orders/Orders.Client"]));
        g.Packages.Add(new PackageNode("pkg:Serilog", "Serilog", []));
        g.Edges.Add(new Edge(EdgeKind.Produces, "proj:orders/Orders.Client", "pkg:Orders.Client", Confidence.Certain) { Version = "3.4.1" });
        g.Edges.Add(new Edge(EdgeKind.References, "proj:billing/Billing.Api", "pkg:Orders.Client", Confidence.Certain) { Version = "3.2.0" });
        g.Edges.Add(new Edge(EdgeKind.References, "proj:gateway/Gateway.Api", "pkg:Orders.Client", Confidence.Certain) { Version = "3.4.1" });
        g.Edges.Add(new Edge(EdgeKind.References, "proj:a/A", "pkg:Serilog", Confidence.Certain) { Version = "3.0.0" });
        g.Edges.Add(new Edge(EdgeKind.References, "proj:b/B", "pkg:Serilog", Confidence.Certain) { Version = "4.0.0" });

        GraphDiagnostics.Add(g);

        var drift = g.Diagnostics.Where(d => d.Kind == DiagnosticKinds.VersionDrift).ToList();
        Assert.Single(drift); // external packages are not checked
        Assert.Equal(["pkg:Orders.Client", "proj:billing/Billing.Api"], drift[0].NodeIds);
        Assert.Contains("3.2.0", drift[0].Message);
        Assert.Contains("3.4.1", drift[0].Message);
    }

    [Fact]
    public void VersionDrift_UsesHighestReference_WhenProducerVersionUnknown_AndSkipsUnresolved()
    {
        var g = new DepGraph();
        g.Packages.Add(new PackageNode("pkg:Lib", "Lib", ["proj:lib/Lib"]));
        g.Edges.Add(new Edge(EdgeKind.Produces, "proj:lib/Lib", "pkg:Lib", Confidence.Certain));
        g.Edges.Add(new Edge(EdgeKind.References, "proj:a/A", "pkg:Lib", Confidence.Certain) { Version = "1.10.0" });
        g.Edges.Add(new Edge(EdgeKind.References, "proj:b/B", "pkg:Lib", Confidence.Certain) { Version = "1.9.0" });
        g.Edges.Add(new Edge(EdgeKind.References, "proj:c/C", "pkg:Lib", Confidence.Certain) { Version = "unresolved($(X))" });

        GraphDiagnostics.Add(g);

        Assert.Equal(["pkg:Lib", "proj:b/B"], g.Diagnostics.Single(d => d.Kind == DiagnosticKinds.VersionDrift).NodeIds);
    }

    [Fact]
    public void Cycles_BetweenRepos()
    {
        var g = new DepGraph();
        g.Edges.Add(new Edge(EdgeKind.DependsOn, "repo:orders", "repo:billing", Confidence.Certain));
        g.Edges.Add(new Edge(EdgeKind.DependsOn, "repo:billing", "repo:orders", Confidence.Certain));
        g.Edges.Add(new Edge(EdgeKind.DependsOn, "repo:gateway", "repo:orders", Confidence.Certain));

        GraphDiagnostics.Add(g);

        var cycle = g.Diagnostics.Single(d => d.Kind == DiagnosticKinds.Cycle);
        Assert.Equal(["repo:billing", "repo:orders"], cycle.NodeIds);
        Assert.Equal("Circular dependency: billing → orders → billing", cycle.Message);
    }

    [Fact]
    public void Unused_Endpoints_ClientMethods_Models()
    {
        var g = new DepGraph();
        g.Projects.Add(new ProjectNode("proj:orders/Orders.Client", "orders", "Orders.Client", "p", ProjectKind.Client, null, null, null, true));
        g.Endpoints.Add(new EndpointNode("ep:orders:GET:/a", "orders", "proj:orders/Orders.Api", "GET", "/a", "a", "C.A", [], [], Loc));
        g.Endpoints.Add(new EndpointNode("ep:orders:GET:/b", "orders", "proj:orders/Orders.Api", "GET", "/b", "b", "C.B", [], [], Loc));
        g.Endpoints.Add(new EndpointNode("ep:public:GET:/c", "public", "proj:public/Api", "GET", "/c", "c", "C.C", [], [], Loc));
        g.ClientMethods.Add(new ClientMethodNode("cm:Orders.Client:I.A", "orders", "proj:orders/Orders.Client", "I", "A", "", "GET", "/a", "a", "refit", Confidence.High, Loc));
        g.ClientMethods.Add(new ClientMethodNode("cm:Orders.Client:I.B", "orders", "proj:orders/Orders.Client", "I", "B", "", "GET", "/b", "b", "refit", Confidence.High, Loc));
        g.ClientMethods.Add(new ClientMethodNode("cm:Orders.Client:Impl.B", "orders", "proj:orders/Orders.Client", "Impl", "B", "", "GET", "/b", "b", "refit", Confidence.High, Loc));
        g.CallSites.Add(new CallSiteNode("cs:x/X:C.M:1", "x", "proj:x/X", "C.M", Confidence.Medium, Loc));
        g.Edges.Add(new Edge(EdgeKind.Invokes, "cs:x/X:C.M:1", "cm:Orders.Client:I.B", Confidence.Medium));
        g.Models.Add(new ModelNode("model:Orders.Client:Used", "orders", "proj:orders/Orders.Client", "Used", ModelKind.Class, [], null, Loc));
        g.Models.Add(new ModelNode("model:Orders.Client:Unused", "orders", "proj:orders/Orders.Client", "Unused", ModelKind.Class, [], null, Loc));
        g.Edges.Add(new Edge(EdgeKind.Targets, "cm:Orders.Client:I.A", "ep:orders:GET:/a", Confidence.High));
        g.Edges.Add(new Edge(EdgeKind.Returns, "ep:orders:GET:/a", "model:Orders.Client:Used", Confidence.High));

        GraphDiagnostics.Add(g);

        Assert.Equal(["ep:orders:GET:/b"], g.Diagnostics.Where(d => d.Kind == DiagnosticKinds.UnusedEndpoint).SelectMany(d => d.NodeIds));
        Assert.Equal(["cm:Orders.Client:I.A"], g.Diagnostics.Where(d => d.Kind == DiagnosticKinds.UnusedClientMethod).SelectMany(d => d.NodeIds));
        Assert.Equal(["model:Orders.Client:Unused"], g.Diagnostics.Where(d => d.Kind == DiagnosticKinds.UnusedModel).SelectMany(d => d.NodeIds));
    }
}
