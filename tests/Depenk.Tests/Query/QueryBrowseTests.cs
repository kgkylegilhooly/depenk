using Depenk.Core.Model;
using Depenk.Query;
using Depenk.Tests.TestUtil;

namespace Depenk.Tests.Query;

public class QueryBrowseTests
{
    private static readonly QueryService Q = FixtureGraph.Query;

    [Fact]
    public void ListRepos_WithLinks()
    {
        var r = Q.ListRepos();
        Assert.Equal(["billing", "customers", "gateway", "orders", "shared"], r.Repos.Select(x => x.Name));
        Assert.Equal(6, r.Links.Count);
        var billing = r.Repos.Single(x => x.Name == "billing");
        Assert.Equal(["repo:customers", "repo:orders"], billing.DependsOn);
        Assert.Equal(["repo:billing", "repo:gateway"], r.Repos.Single(x => x.Name == "orders").DependedOnBy);
        Assert.Equal(1, r.Links.Single(l => l.From == "repo:billing" && l.To == "repo:orders").CallCount);
    }

    [Fact]
    public void GetRepo_PublishesConsumesAndDependents()
    {
        var r = Q.GetRepo("orders");
        Assert.Equal(["Orders.Api", "Orders.Client", "Orders.Tests"], r.Projects.Select(p => p.Name));
        Assert.Equal(("Orders.Client", "3.4.1"), (r.Publishes.Single().PackageId, r.Publishes.Single().Version));
        Assert.Equal(["Billing.Client", "Customers.Client", "Shared.Kernel"],
            r.Consumes.Select(c => c.PackageId).Distinct().Order());
        Assert.Equal("billing", r.Consumes.First(c => c.PackageId == "Billing.Client").ProducerRepo);
        Assert.DoesNotContain(r.Consumes, c => c.PackageId == "Acme.Http");
        Assert.Equal(["repo:billing", "repo:gateway"], r.DependedOnBy.Select(l => l.From));
        Assert.Equal(4, r.Endpoints);
    }

    [Fact]
    public void FindEndpoints_MatchesRouteOrHandler_AndCountsCallers()
    {
        var r = Q.FindEndpoints("orders");
        Assert.Equal(4, r.Items.Count);
        Assert.All(r.Items, e => Assert.Equal("orders", e.Repo));
        Assert.Equal(2, r.Items.Single(e => e.Id == "ep:orders:GET:/api/orders/{id}").Callers);
        Assert.False(r.Truncated);
    }

    [Fact]
    public void FindEndpoints_IsCapped_WithHint()
    {
        var r = Q.FindEndpoints(null, limit: 2);
        Assert.Equal((2, 7, true), (r.Items.Count, r.Total, r.Truncated));
        Assert.Equal("Narrow with query, repo or verb.", r.Hint);
        Assert.Equal(2, Q.FindEndpoints("", repo: "customers", verb: "get").Items.Count);
    }

    [Fact]
    public void GetEndpoint_ContractClientsAndCallers()
    {
        var e = Q.GetEndpoint("GET /api/orders/{id}");
        Assert.Equal("ep:orders:GET:/api/orders/{id}", e.Id);
        Assert.Equal(("id", "route"), (e.Parameters.Single().Name, e.Parameters.Single().Source));
        var ok = e.Returns.Single(r => r.StatusCode == 200);
        Assert.Equal("model:Orders.Client:Acme.Orders.Client.OrderDto", ok.Models.Single().Id);
        Assert.Equal(["cm:Orders.Client:IOrdersClient.GetOrderAsync", "cm:Orders.Client:OrdersClient.GetOrderAsync"],
            e.ClientMethods.Select(c => c.Id));
        Assert.All(e.ClientMethods, c => Assert.Equal(("Orders.Client", "configured-wrapper"), (c.PackageId, c.Strategy)));
        var caller = e.Callers.Single(c => c.Id.StartsWith("cs:billing/Billing.Api:InvoiceBuilder.BuildAsync:"));
        Assert.Equal("cm:Orders.Client:IOrdersClient.GetOrderAsync", caller.ClientMethodId);
        Assert.Equal(Confidence.Medium, caller.Confidence);

        var post = Q.GetEndpoint("POST /api/orders");
        Assert.Equal(("model:Orders.Client:Acme.Orders.Client.CreateOrderRequest", "body"),
            (post.Accepts.Single().Id, post.Accepts.Single().Source));
    }

    [Fact]
    public void GetEndpoint_Unknown_IsNotFound()
    {
        var ex = Assert.Throws<QueryException>(() => Q.GetEndpoint("GET /api/nope"));
        Assert.Equal("not_found", ex.Code);
    }

    [Fact]
    public void GetDiagnostics_FiltersAndCounts()
    {
        Assert.Equal("pkg:Orders.Client", Q.GetDiagnostics(kind: "versionDrift").Items.Single().NodeIds[0]);
        var billing = Q.GetDiagnostics(repo: "billing");
        Assert.Contains(billing.Items, d => d.Kind == DiagnosticKinds.ParseError);
        Assert.Contains(billing.Items, d => d.Kind == DiagnosticKinds.Cycle);
        Assert.Equal(billing.Total, billing.ByKind.Sum(k => k.Count));
        Assert.True(Q.GetDiagnostics(limit: 1).Truncated);
    }
}
