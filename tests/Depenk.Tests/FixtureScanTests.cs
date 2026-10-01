using Depenk.Analysis;
using Depenk.Core;
using Depenk.Core.Model;
using Depenk.Tests.TestUtil;
using static VerifyXunit.Verifier;

namespace Depenk.Tests;

public class FixtureScanTests
{
    private static readonly string[] RepoNames = ["billing", "customers", "gateway", "orders", "shared"];

    internal static TempWorkspace CopyFixture()
    {
        var ws = new TempWorkspace();
        var src = Path.Combine(AppContext.BaseDirectory, "fixtures", "workspace");
        foreach (var file in Directory.EnumerateFiles(src, "*", SearchOption.AllDirectories))
            ws.File(Path.GetRelativePath(src, file).Replace('\\', '/'), File.ReadAllText(file));
        foreach (var r in RepoNames) ws.Repo(r);
        return ws;
    }

    private static (DepGraph Graph, string Json, string Root) ScanFixture()
    {
        using var ws = CopyFixture();
        var g = new ScanOrchestrator().Scan(ws.Root);
        g.GeneratedAt = DateTimeOffset.UnixEpoch;
        return (g, GraphJson.Serialize(g), ws.Root);
    }

    [Fact]
    public void RepoDependencies()
    {
        var (g, _, _) = ScanFixture();
        Assert.Equal(
            ["billing→customers", "billing→orders", "gateway→orders", "orders→billing", "orders→customers", "orders→shared"],
            g.EdgesOf(EdgeKind.DependsOn).Select(e => $"{e.From[5..]}→{e.To[5..]}"));
        Assert.Equal(1, g.EdgesOf(EdgeKind.DependsOn).Single(e => e.From == "repo:billing" && e.To == "repo:orders").CallCount);
        Assert.Equal(2, g.EdgesOf(EdgeKind.DependsOn).Single(e => e.From == "repo:gateway").CallCount);
    }

    [Fact]
    public void ProjectKinds()
    {
        var (g, _, _) = ScanFixture();
        var kinds = g.Projects.ToDictionary(p => p.Name, p => p.Kind);
        Assert.Equal(ProjectKind.Api, kinds["Orders.Api"]);
        Assert.Equal(ProjectKind.Client, kinds["Orders.Client"]);
        Assert.Equal(ProjectKind.Client, kinds["Customers.Client"]);
        Assert.Equal(ProjectKind.Client, kinds["Billing.Client"]);
        Assert.Equal(ProjectKind.Library, kinds["Shared.Kernel"]);
        Assert.Equal(ProjectKind.Test, kinds["Orders.Tests"]);
        Assert.Equal("3.4.1", g.Projects.Single(p => p.Name == "Orders.Client").Version);
    }

    [Fact]
    public void EndToEndFlow_CallSiteToEndpointToModel()
    {
        var (g, _, _) = ScanFixture();
        var target = g.EdgesOf(EdgeKind.Targets).Single(e => e.From == "cm:Orders.Client:IOrdersClient.GetOrderAsync");
        Assert.Equal(("ep:orders:GET:/api/orders/{id}", "configured-wrapper"), (target.To, target.Strategy));
        Assert.Contains(g.EdgesOf(EdgeKind.Invokes), e => e.From.StartsWith("cs:billing/Billing.Api:InvoiceBuilder.BuildAsync:")
                                                          && e.To == "cm:Orders.Client:IOrdersClient.GetOrderAsync");
        Assert.Contains(g.EdgesOf(EdgeKind.Returns), e => e.From == "ep:orders:GET:/api/orders/{id}"
                                                          && e.To == "model:Orders.Client:Acme.Orders.Client.OrderDto");
        Assert.Contains(g.EdgesOf(EdgeKind.FieldOf), e => e.From == "model:Orders.Client:Acme.Orders.Client.OrderDto"
                                                          && e.To == "model:Customers.Client:Acme.Customers.Client.CustomerDto");
        Assert.Contains(g.EdgesOf(EdgeKind.FieldOf), e => e.To == "model:Shared.Kernel:Acme.Shared.Money");
        Assert.Contains(g.EdgesOf(EdgeKind.Targets), e => e.From == "cm:Billing.Client:IBillingApi.CreateInvoice"
                                                          && e.To == "ep:billing:POST:/api/invoices");
    }

    [Fact]
    public void Diagnostics()
    {
        var (g, _, _) = ScanFixture();
        string[] Of(string kind) => g.Diagnostics.Where(d => d.Kind == kind).Select(d => d.NodeIds[0]).ToArray();

        Assert.Equal(["pkg:Orders.Client"], Of(DiagnosticKinds.VersionDrift));
        Assert.Equal(["repo:billing"], Of(DiagnosticKinds.Cycle));
        Assert.Equal(["cm:Customers.Client:CustomersClient.GetCustomerAsync"], Of(DiagnosticKinds.AmbiguousRoute));
        Assert.Equal(["ep:orders:DELETE:/api/orders/{id}"], Of(DiagnosticKinds.UnusedEndpoint));
        Assert.Empty(Of(DiagnosticKinds.UnusedClientMethod));
        var parse = g.Diagnostics.Single(d => d.Kind == DiagnosticKinds.ParseError);
        Assert.StartsWith("billing/src/Billing.Api/Broken.cs: line 3:", parse.Message);
    }

    [Fact]
    public void Json_IsPortable_AndDeterministic()
    {
        var (_, json1, root) = ScanFixture();
        var (_, json2, _) = ScanFixture();
        Assert.Equal(json1, json2);
        Assert.DoesNotContain("\\\\", json1);
        Assert.DoesNotContain(root.Replace('\\', '/'), json1);
        Assert.DoesNotContain(root, json1);
    }

    [Fact]
    public Task Snapshot() => Verify(ScanFixture().Json, extension: "json").UseDirectory("Snapshots");
}
