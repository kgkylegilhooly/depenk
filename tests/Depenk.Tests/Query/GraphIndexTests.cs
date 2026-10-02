using Depenk.Core.Model;
using Depenk.Query;
using Depenk.Tests.TestUtil;

namespace Depenk.Tests.Query;

public class GraphIndexTests
{
    private static readonly GraphIndex Ix = FixtureGraph.Index;

    [Fact]
    public void IndexesEveryNode()
    {
        var g = FixtureGraph.Value;
        var expected = g.Repos.Count + g.Projects.Count + g.Packages.Count + g.Endpoints.Count
                       + g.ClientMethods.Count + g.CallSites.Count + g.Models.Count;
        Assert.Equal(expected, Ix.Nodes.Count);
        Assert.Equal(NodeKind.Endpoint, Ix.Get("ep:orders:GET:/api/orders/{id}").Kind);
        Assert.Equal("GET /api/orders/{id}", Ix.Get("ep:orders:GET:/api/orders/{id}").Label);
        Assert.Equal("orders", Ix.Get("pkg:Orders.Client").Repo);
        Assert.Null(Ix.Get("pkg:Refit").Repo);
    }

    [Fact]
    public void Adjacency_IsNormalizedToDependentToDependency()
    {
        Assert.Equal(["repo:customers", "repo:orders"],
            Ix.DependenciesOf("repo:billing").Where(h => h.Kind == EdgeKind.DependsOn).Select(h => h.To).Order());

        var pkgDependents = Ix.DependentsOf("pkg:Orders.Client").Select(h => h.From).ToList();
        Assert.Contains("proj:billing/Billing.Api", pkgDependents);
        Assert.Contains("proj:gateway/Gateway.Api", pkgDependents);

        // produces is flipped: the package depends on the project that produces it
        var produced = Ix.DependenciesOf("pkg:Orders.Client").Single(h => h.Kind == EdgeKind.Produces);
        Assert.Equal("proj:orders/Orders.Client", produced.To);
        Assert.Equal(EdgeKind.Produces, produced.Edge.Kind);
    }

    [Fact]
    public void ResolvesEndpoints_ByVerbAndRoute_CaseInsensitive()
    {
        Assert.Equal("ep:orders:GET:/api/orders/{id}", Ix.ResolveEndpoint("get /API/orders/{id}"));
        Assert.Equal("ep:orders:GET:/api/orders/{id}", Ix.ResolveEndpoint("ep:orders:GET:/api/orders/{id}"));
        Assert.Equal("ep:orders:GET:/api/orders/{id}", Ix.ResolveEndpoint("GET /api/orders/{orderId}")); // normalized
    }

    [Fact]
    public void AmbiguousEndpoint_ListsCandidates()
    {
        var ex = Assert.Throws<QueryException>(() => Ix.ResolveEndpoint("GET /api/customers/{x}"));
        Assert.Equal("ambiguous", ex.Code);
        Assert.Equal(["ep:customers:GET:/api/customers/{id:guid}", "ep:customers:GET:/api/customers/{slug}"],
            ex.Suggestions!.Order());
    }

    [Fact]
    public void ResolvesModels_ByIdFullNameOrSimpleName()
    {
        const string id = "model:Orders.Client:Acme.Orders.Client.OrderDto";
        Assert.Equal(id, Ix.ResolveModel(id));
        Assert.Equal(id, Ix.ResolveModel("Acme.Orders.Client.OrderDto"));
        Assert.Equal(id, Ix.ResolveModel("OrderDto"));
    }

    [Fact]
    public void UnknownId_IsNotFound_WithSuggestions()
    {
        var ex = Assert.Throws<QueryException>(() => Ix.Get("ep:orders:GET:/api/order/{id}"));
        Assert.Equal("not_found", ex.Code);
        Assert.Equal("ep:orders:GET:/api/orders/{id}", ex.Suggestions![0]);
        Assert.False(string.IsNullOrEmpty(ex.Hint));
    }

    [Fact]
    public void ResolveAny_PrefersRepoName_ThenPackage_ThenModel()
    {
        Assert.Equal("repo:orders", Ix.ResolveAny("orders"));
        Assert.Equal("pkg:Orders.Client", Ix.ResolveAny("orders.client"));
        Assert.Equal("model:Shared.Kernel:Acme.Shared.Money", Ix.ResolveAny("Money"));
        Assert.Equal("not_found", Assert.Throws<QueryException>(() => Ix.ResolveAny("zzz-nothing")).Code);
    }

    [Theory]
    [InlineData("kitten", "sitting", 3)]
    [InlineData("", "abc", 3)]
    [InlineData("same", "same", 0)]
    public void Levenshtein(string a, string b, int d) => Assert.Equal(d, Fuzzy.Distance(a, b));
}
