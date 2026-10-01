using Depenk.Analysis.Linking;
using Depenk.Core.Model;

namespace Depenk.Tests.Analysis;

public class ClientEndpointLinkerTests
{
    private static readonly SourceLocation Loc = new("x.cs", 1);

    private static EndpointNode Ep(string repo, string verb, string route, string norm) =>
        new($"ep:{repo}:{verb}:{route}", repo, $"proj:{repo}/Api", verb, route, norm, "C.M", [], [], Loc);

    private static ClientMethodNode Cm(string repo, string name, string? verb, string? norm,
        string strategy = "refit", Confidence c = Confidence.High) =>
        new($"cm:Client:I.{name}", repo, $"proj:{repo}/Client", "I", name, "", verb, norm is null ? null : "/" + norm,
            norm, strategy, c, Loc);

    [Fact]
    public void ExactMatch_SameRepoOnly()
    {
        var g = new DepGraph();
        g.Endpoints.Add(Ep("orders", "GET", "/api/orders/{id}", "api/orders/{}"));
        g.Endpoints.Add(Ep("billing", "GET", "/api/orders/{id}", "api/orders/{}"));
        g.ClientMethods.Add(Cm("orders", "Get", "GET", "api/orders/{}"));

        ClientEndpointLinker.Link(g);

        var e = g.EdgesOf(EdgeKind.Targets).Single();
        Assert.Equal(("cm:Client:I.Get", "ep:orders:GET:/api/orders/{id}", Confidence.High, "refit"),
            (e.From, e.To, e.Confidence, e.Strategy));
        Assert.Empty(g.Diagnostics);
    }

    [Fact]
    public void VerbMustMatch_CaseInsensitive()
    {
        var g = new DepGraph();
        g.Endpoints.Add(Ep("orders", "POST", "/api/orders", "api/orders"));
        g.ClientMethods.Add(Cm("orders", "Create", "post", "api/orders"));
        g.ClientMethods.Add(Cm("orders", "Wrong", "PUT", "api/orders"));

        ClientEndpointLinker.Link(g);

        Assert.Equal("cm:Client:I.Create", g.EdgesOf(EdgeKind.Targets).Single().From);
        Assert.Contains(g.Diagnostics, d => d.Kind == DiagnosticKinds.UnresolvedClientMethod && d.NodeIds.Contains("cm:Client:I.Wrong"));
    }

    [Fact]
    public void Ambiguous_KeepsAllAsLow_WithDiagnostic()
    {
        var g = new DepGraph();
        g.Endpoints.Add(Ep("orders", "GET", "/api/orders/{id}", "api/orders/{}"));
        g.Endpoints.Add(Ep("orders", "GET", "/api/orders/{slug}", "api/orders/{}"));
        g.ClientMethods.Add(Cm("orders", "Get", "GET", "api/orders/{}"));

        ClientEndpointLinker.Link(g);

        Assert.Equal(2, g.EdgesOf(EdgeKind.Targets).Count());
        Assert.All(g.EdgesOf(EdgeKind.Targets), e => Assert.Equal(Confidence.Low, e.Confidence));
        Assert.Contains(g.Diagnostics, d => d.Kind == DiagnosticKinds.AmbiguousRoute);
    }

    [Fact]
    public void SuffixMatch_IsLowConfidence()
    {
        var g = new DepGraph();
        g.Endpoints.Add(Ep("orders", "GET", "/api/orders/{id}", "api/orders/{}"));
        g.ClientMethods.Add(Cm("orders", "Get", "GET", "orders/{}", "generic-http", Confidence.Medium));

        ClientEndpointLinker.Link(g);

        var e = g.EdgesOf(EdgeKind.Targets).Single();
        Assert.Equal((Confidence.Low, "generic-http+suffix"), (e.Confidence, e.Strategy));
    }

    [Fact]
    public void NoHttpCallDetected_Diagnostic()
    {
        var g = new DepGraph();
        g.ClientMethods.Add(Cm("orders", "Stats", null, null, "none", Confidence.Low));
        ClientEndpointLinker.Link(g);
        Assert.Equal("cm:Client:I.Stats: no HTTP call detected", g.Diagnostics.Single().Message);
    }

    [Fact]
    public void SuffixMatch_ReverseDirection_ClientRouteLonger()
    {
        var g = new DepGraph();
        g.Endpoints.Add(Ep("orders", "GET", "/orders/{id}", "orders/{}"));
        g.ClientMethods.Add(Cm("orders", "Get", "GET", "api/v1/orders/{}", "generic-http", Confidence.Medium));

        ClientEndpointLinker.Link(g);

        var e = g.EdgesOf(EdgeKind.Targets).Single();
        Assert.Equal(("ep:orders:GET:/orders/{id}", Confidence.Low, "generic-http+suffix"), (e.To, e.Confidence, e.Strategy));
        Assert.Empty(g.Diagnostics);
    }

    [Fact]
    public void AmbiguousSuffix_LinksEveryCandidateLow_WithDiagnostic()
    {
        var g = new DepGraph();
        g.Endpoints.Add(Ep("orders", "GET", "/api/orders/{id}", "api/orders/{}"));
        g.Endpoints.Add(Ep("orders", "GET", "/internal/orders/{id}", "internal/orders/{}"));
        g.ClientMethods.Add(Cm("orders", "Get", "GET", "orders/{}", "generic-http", Confidence.Medium));

        ClientEndpointLinker.Link(g);

        var edges = g.EdgesOf(EdgeKind.Targets).ToList();
        Assert.Equal(["ep:orders:GET:/api/orders/{id}", "ep:orders:GET:/internal/orders/{id}"], edges.Select(e => e.To));
        Assert.All(edges, e => Assert.Equal((Confidence.Low, "generic-http+suffix"), (e.Confidence, e.Strategy)));
        var d = Assert.Single(g.Diagnostics);
        Assert.Equal((DiagnosticKinds.AmbiguousRoute, Severities.Warning), (d.Kind, d.Severity));
        Assert.Equal(["cm:Client:I.Get", "ep:orders:GET:/api/orders/{id}", "ep:orders:GET:/internal/orders/{id}"], d.NodeIds);
        Assert.Equal("cm:Client:I.Get (GET /orders/{}) matches 2 endpoints by route suffix: " +
                     "ep:orders:GET:/api/orders/{id}, ep:orders:GET:/internal/orders/{id}", d.Message);
    }

    [Fact]
    public void AmbiguousExact_SeverityAndNodeIds()
    {
        var g = new DepGraph();
        g.Endpoints.Add(Ep("orders", "GET", "/api/orders/{id}", "api/orders/{}"));
        g.Endpoints.Add(Ep("orders", "GET", "/api/orders/{slug}", "api/orders/{}"));
        g.ClientMethods.Add(Cm("orders", "Get", "GET", "api/orders/{}"));

        ClientEndpointLinker.Link(g);

        var d = Assert.Single(g.Diagnostics);
        Assert.Equal(Severities.Warning, d.Severity);
        Assert.Equal(["cm:Client:I.Get", "ep:orders:GET:/api/orders/{id}", "ep:orders:GET:/api/orders/{slug}"], d.NodeIds);
        Assert.All(g.EdgesOf(EdgeKind.Targets), e => Assert.Equal("refit", e.Strategy));
    }

    [Fact]
    public void Unresolved_IsInfo_WithMessage_AndFallsBackToNormalizedRoute()
    {
        var g = new DepGraph();
        g.Endpoints.Add(Ep("orders", "GET", "/api/orders/{id}", "api/orders/{}"));
        g.ClientMethods.Add(Cm("orders", "Find", "GET", "api/customers/{}") with { Route = null });

        ClientEndpointLinker.Link(g);

        Assert.Empty(g.EdgesOf(EdgeKind.Targets));
        var d = Assert.Single(g.Diagnostics);
        Assert.Equal((DiagnosticKinds.UnresolvedClientMethod, Severities.Info), (d.Kind, d.Severity));
        Assert.Equal(["cm:Client:I.Find"], d.NodeIds);
        Assert.Equal("cm:Client:I.Find: no endpoint in repo 'orders' matches GET api/customers/{}", d.Message);
    }
}
