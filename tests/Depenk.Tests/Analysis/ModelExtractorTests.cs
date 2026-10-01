using Depenk.Analysis;
using Depenk.Analysis.Endpoints;
using Depenk.Analysis.Models;
using Depenk.Core.Model;
using Depenk.Tests.TestUtil;

namespace Depenk.Tests.Analysis;

public class ModelExtractorTests
{
    private const string Api = """
        namespace Acme.Orders.Api;
        [Route("api/orders")]
        public class OrdersController : ControllerBase
        {
            [HttpGet("{id}")] public Task<ActionResult<OrderDto>> Get(Guid id) => null!;
            [HttpPost] public Task<ActionResult<OrderDto>> Create(CreateOrderRequest request) => null!;
            [HttpGet("tree")] public Node Tree() => null!;
            [HttpGet("mystery")] public Mystery Unknown() => null!;
        }
        public class Node { public Node? Parent { get; set; } public List<Node> Children { get; set; } = []; }
        """;

    private const string Client = """
        namespace Acme.Orders.Client;
        public record CreateOrderRequest(Guid CustomerId, List<OrderLineDto> Lines);
        public class OrderDto : EntityBase
        {
            public OrderStatus Status { get; init; }
            public CustomerRef? Customer { get; init; }
            public List<OrderLineDto> Lines { get; init; } = [];
            private int Hidden { get; set; }
            public static OrderDto Empty { get; } = new();
        }
        public class EntityBase { public Guid Id { get; init; } }
        public class OrderLineDto { public string Sku { get; set; } = ""; public int Qty { get; set; } }
        public enum OrderStatus { Pending, Paid, Shipped }
        public class UnusedDto { public int X { get; set; } }
        """;

    private const string Customers = """
        namespace Acme.Customers.Client;
        public class CustomerRef { public Guid Id { get; init; } public string Name { get; init; } = ""; }
        """;

    private static DepGraph Run()
    {
        var api = Src.SetFor("orders", "Orders.Api", ("orders/Api/OrdersController.cs", Api));
        var client = Src.SetFor("orders", "Orders.Client", ("orders/Client/Models.cs", Client));
        var customers = Src.SetFor("customers", "Customers.Client", ("customers/Client/CustomerRef.cs", Customers));
        var g = new DepGraph();
        g.Endpoints.AddRange(new ControllerEndpointFinder().Find(api));
        var refs = new Dictionary<string, IReadOnlyList<string>> { ["proj:orders/Orders.Api"] = ["proj:orders/Orders.Client"] };
        new ModelExtractor([api, client, customers], id => refs.GetValueOrDefault(id, []))
            .Extract(g, new HashSet<string> { "proj:orders/Orders.Client", "proj:customers/Customers.Client" });
        return g;
    }

    private static ModelNode M(DepGraph g, string fullName) => g.Models.Single(m => m.FullName == fullName);

    [Fact]
    public void BuildsModelTree_AcrossProjectsAndRepos()
    {
        var g = Run();
        var order = M(g, "Acme.Orders.Client.OrderDto");
        Assert.Equal("model:Orders.Client:Acme.Orders.Client.OrderDto", order.Id);
        Assert.Equal(ModelKind.Class, order.Kind);
        Assert.Equal(["Status", "Customer", "Lines", "Id"], order.Fields.Select(f => f.Name));
        Assert.Equal(new ModelField("Customer", "CustomerRef?", true, false), order.Fields[1]);
        Assert.Equal(new ModelField("Lines", "List<OrderLineDto>", false, true), order.Fields[2]);

        var customer = M(g, "Acme.Customers.Client.CustomerRef");
        Assert.Equal("customers", customer.Repo);
        Assert.Contains(g.EdgesOf(EdgeKind.FieldOf), e => e.From == order.Id && e.To == customer.Id && e.FieldName == "Customer");

        Assert.Equal(["Pending", "Paid", "Shipped"], M(g, "Acme.Orders.Client.OrderStatus").EnumValues);
        var req = M(g, "Acme.Orders.Client.CreateOrderRequest");
        Assert.Equal(ModelKind.Record, req.Kind);
        Assert.Equal(["CustomerId", "Lines"], req.Fields.Select(f => f.Name));
    }

    [Fact]
    public void LinksEndpointsToModels()
    {
        var g = Run();
        var ret = g.EdgesOf(EdgeKind.Returns).Single(e => e.From == "ep:orders:GET:/api/orders/{id}");
        Assert.Equal(("model:Orders.Client:Acme.Orders.Client.OrderDto", 200), (ret.To, ret.StatusCode));
        var acc = g.EdgesOf(EdgeKind.Accepts).Single(e => e.From == "ep:orders:POST:/api/orders");
        Assert.Equal(("model:Orders.Client:Acme.Orders.Client.CreateOrderRequest", "body"), (acc.To, acc.Source));
        Assert.DoesNotContain(g.EdgesOf(EdgeKind.Accepts), e => e.From == "ep:orders:GET:/api/orders/{id}"); // Guid is simple
    }

    [Fact]
    public void CyclicModels_Terminate_AndUnknownTypesAreOpaque()
    {
        var g = Run();
        var node = M(g, "Acme.Orders.Api.Node");
        Assert.Contains(g.EdgesOf(EdgeKind.FieldOf), e => e.From == node.Id && e.To == node.Id && e.FieldName == "Parent");
        var mystery = g.Models.Single(m => m.Id == "model:?:Mystery");
        Assert.Equal((ModelKind.Opaque, ""), (mystery.Kind, mystery.Repo));
    }

    [Fact]
    public void ContractSurface_IncludesUnreferencedClientModels_WithoutDuplicates()
    {
        var g = Run();
        Assert.Single(g.Models, m => m.FullName == "Acme.Orders.Client.UnusedDto");
        Assert.Equal(g.Models.Count, g.Models.Select(m => m.Id).Distinct().Count());
        Assert.Equal(g.Edges.Count, g.Edges.Distinct().Count());
    }
}
