using Depenk.Analysis.CallSites;
using Depenk.Core.Model;
using Depenk.Tests.TestUtil;

namespace Depenk.Tests.Analysis;

public class CallSiteFinderTests
{
    private static readonly SourceLocation Loc = new("x.cs", 1);

    private static ClientMethodNode Cm(string type, string method) =>
        new($"cm:Orders.Client:{type}.{method}", "orders", "proj:orders/Orders.Client", type, method, "",
            "GET", "/api/orders/{id}", "api/orders/{}", "refit", Confidence.High, Loc);

    private const string Consumer = """
        namespace Acme.Billing.Api;
        public class InvoiceBuilder(Acme.Orders.IOrdersClient orders)
        {
            private readonly IOrdersClient? _backup;

            public async Task Build(Guid id)
            {
                var order = await orders.GetOrderAsync(id);
                await _backup?.GetOrderAsync(id)!;
                var other = new OtherClient();
                other.GetOrderAsync(id);
            }

            public int Count => orders.ListAsync(1).Result.Count;
        }
        """;

    [Fact]
    public void FindsCallsOnClientTypedReceivers()
    {
        var scan = CallSiteFinder.Find(
            Src.SetFor("billing", "Billing.Api", ("billing/Api/InvoiceBuilder.cs", Consumer)),
            [Cm("IOrdersClient", "GetOrderAsync"), Cm("IOrdersClient", "ListAsync")]);

        Assert.Equal(["InvoiceBuilder.Build", "InvoiceBuilder.Build", "InvoiceBuilder.Count"],
            scan.CallSites.Select(c => c.ContainingMember));
        var first = scan.CallSites[0];
        Assert.Equal("cs:billing/Billing.Api:InvoiceBuilder.Build:8", first.Id);
        Assert.Equal(new SourceLocation("billing/Api/InvoiceBuilder.cs", 8), first.Location);
        Assert.Equal(Confidence.Medium, first.Confidence);

        Assert.Equal(3, scan.Invokes.Count);
        Assert.All(scan.Invokes, e => Assert.Equal((EdgeKind.Invokes, Confidence.Medium), (e.Kind, e.Confidence)));
        Assert.Equal("cm:Orders.Client:IOrdersClient.ListAsync", scan.Invokes[2].To);
    }

    [Fact]
    public void SameTypeNameInSeveralPackages_IsDisambiguatedByNamespace()
    {
        var a = new ClientMethodNode("cm:A.Client:R0Client.Get", "a", "proj:a/A.Client", "R0Client", "Get", "",
            "GET", "/x", "x", "refit", Confidence.High, Loc);
        var b = a with { Id = "cm:B.Client:R0Client.Get", Repo = "b", ProjectId = "proj:b/B.Client#2" };

        var scan = CallSiteFinder.Find(Src.SetFor("c", "C.Api", ("c.cs", """
            using Acme.B.Client;
            public class U(R0Client viaUsing, A.Client.R0Client qualified)
            {
                public void M() { viaUsing.Get(); qualified.Get(); }
            }
            """)), [a, b]);

        Assert.Equal(["cm:B.Client:R0Client.Get", "cm:A.Client:R0Client.Get"], scan.Invokes.Select(e => e.To));
    }

    [Fact]
    public void NoReachableMethods_NoCallSites()
    {
        var scan = CallSiteFinder.Find(Src.SetFor("billing", "Billing.Api", ("a.cs", Consumer)), []);
        Assert.Empty(scan.CallSites);
    }
}
