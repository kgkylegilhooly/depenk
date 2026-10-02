using Depenk.Core.Model;
using Depenk.Query;
using Depenk.Tests.TestUtil;

namespace Depenk.Tests.Query;

public class QueryAnalyzeTests
{
    private static readonly QueryService Q = FixtureGraph.Query;
    private const string OrderDto = "model:Orders.Client:Acme.Orders.Client.OrderDto";
    private const string CustomerDto = "model:Customers.Client:Acme.Customers.Client.CustomerDto";
    private static readonly string[] OrderEndpoints =
        ["ep:orders:GET:/api/orders", "ep:orders:GET:/api/orders/{id}", "ep:orders:POST:/api/orders"];

    [Fact]
    public void GetModel_ExpandsTwoLevels_AndMarksCrossRepo()
    {
        var m = Q.GetModel("OrderDto");
        Assert.Equal(["Id", "Status", "Customer", "Lines"], m.Fields.Select(f => f.Name));
        var customer = m.Fields.Single(f => f.Name == "Customer");
        Assert.True(customer.CrossRepo);
        Assert.Equal(CustomerDto, customer.Types!.Single().Id);
        var line = m.Fields.Single(f => f.Name == "Lines").Types!.Single();
        var money = line.Fields.Single(f => f.Name == "UnitPrice").Types!.Single();
        Assert.Equal("model:Shared.Kernel:Acme.Shared.Money", money.Id);
        Assert.Equal(["Amount", "Currency"], money.Fields.Select(f => f.Name));
        Assert.All(money.Fields, f => Assert.Null(f.Types)); // depth exhausted
        Assert.Null(Q.GetModel("OrderDto", depth: 0).Fields.Single(f => f.Name == "Lines").Types);
    }

    [Fact]
    public void FindModelUsages_IncludesContainingModelsAndConsumerRepos()
    {
        var direct = Q.FindModelUsages("OrderDto");
        Assert.Equal(OrderEndpoints, direct.Endpoints.Select(u => u.EndpointId).Order(StringComparer.Ordinal));
        Assert.Equal(["repo:billing", "repo:gateway", "repo:orders"], direct.Repos);

        var nested = Q.FindModelUsages(CustomerDto);
        Assert.Equal([OrderDto], nested.ContainedIn);
        Assert.All(nested.Endpoints, u => Assert.Equal(OrderDto, u.Via));
        Assert.Equal(["repo:billing", "repo:gateway", "repo:orders"], nested.Repos);
    }

    [Fact]
    public void Trace_DownUpAndBoth()
    {
        Assert.Equal(["repo:customers", "repo:orders"], Q.Trace("billing", "down", 1).Nodes.Select(n => n.Id));

        var up = Q.Trace(OrderDto, "up", 3).Nodes;
        Assert.Contains(up, n => n.Id == "ep:orders:GET:/api/orders/{id}" && n.Depth == 1 && n.ViaKind == EdgeKind.Returns);
        Assert.Contains(up, n => n.Id == "cm:Orders.Client:IOrdersClient.GetOrderAsync" && n.Depth == 2);
        Assert.Contains(up, n => n.Id.StartsWith("cs:billing/Billing.Api:InvoiceBuilder.BuildAsync:")
                                 && n.Depth == 3 && n.Confidence == Confidence.Medium);

        var both = Q.Trace("orders", "both", 1);
        Assert.Contains(both.Nodes, n => n.Direction == "down" && n.Id == "repo:shared");
        Assert.Contains(both.Nodes, n => n.Direction == "up" && n.Id == "repo:gateway");
        Assert.True(Q.Trace("orders", "both", 10, limit: 1).Truncated);
        Assert.Equal("invalid_argument", Assert.Throws<QueryException>(() => Q.Trace("orders", "sideways")).Code);
    }

    [Fact]
    public void ImpactOfModel_ReachesCallSitesInOtherRepos()
    {
        var r = Q.ImpactOfChange("OrderDto");
        Assert.Equal(("model", (string?)null), (r.TargetKind, r.Field));
        Assert.Equal(OrderEndpoints, r.Endpoints.Select(a => a.Id));
        Assert.Equal(6, r.ClientMethods.Count);
        Assert.Equal(3, r.CallSites.Count);
        Assert.All(r.CallSites, c => Assert.Equal(Confidence.Medium, c.Confidence));
        Assert.Equal(["repo:billing", "repo:gateway", "repo:orders"], r.Repos.Select(a => a.Id));
        Assert.Contains(r.Projects, p => p.Id == "proj:gateway/Gateway.Api");
        Assert.DoesNotContain(r.Models, m => m.Id == OrderDto); // start excluded
    }

    [Fact]
    public void ImpactOfNestedModel_And_Field_And_Package()
    {
        Assert.Contains(Q.ImpactOfChange(CustomerDto).Models, m => m.Id == OrderDto);

        var field = Q.ImpactOfChange("OrderDto.Lines");
        Assert.Equal(("field", "Lines"), (field.TargetKind, field.Field));
        Assert.Equal(OrderEndpoints, field.Endpoints.Select(a => a.Id));

        var pkg = Q.ImpactOfChange("Orders.Client");
        Assert.Equal("package", pkg.TargetKind);
        Assert.Equal(["proj:billing/Billing.Api", "proj:gateway/Gateway.Api"], pkg.Projects.Select(p => p.Id));
        Assert.Equal(["repo:billing", "repo:gateway"], pkg.Repos.Select(p => p.Id));

        Assert.Equal("not_found", Assert.Throws<QueryException>(() => Q.ImpactOfChange("OrderDto.Nope")).Code);
    }

    [Fact]
    public void HowToCall_PrefersInterface_AndExplainsMissingClient()
    {
        var r = Q.HowToCall("GET /api/orders/{id}");
        var first = r.Options[0];
        Assert.Equal(("IOrdersClient", "GetOrderAsync", "Orders.Client", "3.4.1"),
            (first.Type, first.Method, first.PackageId, first.LatestVersion));
        Assert.Equal(OrderDto, first.Response.Single(x => x.StatusCode == 200).Models.Single().Id);
        Assert.Null(r.Hint);

        var none = Q.HowToCall("DELETE /api/orders/{id}");
        Assert.Empty(none.Options);
        Assert.NotNull(none.Hint);
    }

    [Fact]
    public void Overview_IsMarkdownSummary()
    {
        var md = Q.Overview();
        Assert.StartsWith("# depenk workspace overview", md);
        Assert.Contains("5 repos", md);
        Assert.Contains("billing → orders", md);
        Assert.Contains("versionDrift", md);
    }
}
