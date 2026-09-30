using Depenk.Core;
using Depenk.Core.Model;

namespace Depenk.Tests.Core;

public class GraphJsonTests
{
    [Fact]
    public void RoundTrips_AllNodeKinds_AndUsesCamelCaseEnums()
    {
        var g = new DepGraph();
        g.Repos.Add(new RepoNode(Ids.Repo("orders"), "orders", "orders", "abc123", false));
        g.Projects.Add(new ProjectNode(Ids.Project("orders", "Orders.Api"), "orders", "Orders.Api",
            "orders/src/Orders.Api/Orders.Api.csproj", ProjectKind.Api, "Microsoft.NET.Sdk.Web", null, null, false));
        g.Packages.Add(new PackageNode(Ids.Package("Orders.Client"), "Orders.Client", [Ids.Project("orders", "Orders.Client")]));
        g.Endpoints.Add(new EndpointNode(Ids.Endpoint("orders", "GET", "/api/orders/{id}"), "orders",
            Ids.Project("orders", "Orders.Api"), "GET", "/api/orders/{id}", "api/orders/{}", "OrdersController.Get",
            [new EndpointParameter("id", "route", "Guid", true, null)], [new ResponseType(200, "OrderDto")],
            new SourceLocation("orders/src/Orders.Api/OrdersController.cs", 12)));
        g.Edges.Add(new Edge(EdgeKind.References, Ids.Project("billing", "Billing.Api"), Ids.Package("Orders.Client"),
            Confidence.Certain) { Version = "3.4.1" });
        g.Diagnostics.Add(new Diagnostic("versionDrift", "warning", [Ids.Package("Orders.Client")], "drift"));

        var json = GraphJson.Serialize(g);
        var back = GraphJson.Deserialize(json);

        Assert.Contains("\"schemaVersion\": 1", json);
        Assert.Contains("\"kind\": \"api\"", json);
        Assert.Contains("\"confidence\": \"certain\"", json);
        Assert.DoesNotContain("\"external\"", json);
        Assert.Equal("api/orders/{}", back.Endpoints[0].NormalizedRoute);
        Assert.Equal("3.4.1", back.Edges[0].Version);
        Assert.Equal(EdgeKind.References, back.Edges[0].Kind);
        Assert.Equal(g.Endpoints[0].Parameters, back.Endpoints[0].Parameters);
    }

    [Fact]
    public void Ids_AreStableAndReadable()
    {
        Assert.Equal("repo:orders", Ids.Repo("orders"));
        Assert.Equal("proj:orders/Orders.Api", Ids.Project("orders", "Orders.Api"));
        Assert.Equal("pkg:Orders.Client", Ids.Package("Orders.Client"));
        Assert.Equal("ep:orders:GET:/api/orders/{id}", Ids.Endpoint("orders", "get", "/api/orders/{id}"));
        Assert.Equal("cm:Orders.Client:IOrdersClient.GetOrderAsync", Ids.ClientMethod("Orders.Client", "IOrdersClient", "GetOrderAsync"));
        Assert.Equal("cs:billing/Billing.Api:InvoiceBuilder.Build:118", Ids.CallSite("billing", "Billing.Api", "InvoiceBuilder", "Build", 118));
        Assert.Equal("model:Orders.Client:Acme.Orders.OrderDto", Ids.Model("Orders.Client", "Acme.Orders.OrderDto"));
    }
}
