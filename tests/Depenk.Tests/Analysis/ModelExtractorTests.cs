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

    private static DepGraph RunSets(int maxDepth, string[] controllerCode, params SourceSet[] others)
    {
        var api = Src.SetFor("r", "Api", ("r/Api/C.cs", string.Join("\n", controllerCode)));
        var g = new DepGraph();
        g.Endpoints.AddRange(new ControllerEndpointFinder().Find(api));
        new ModelExtractor([api, .. others], _ => [], maxDepth).Extract(g, new HashSet<string>());
        return g;
    }

    private const string Ctl = """
        namespace A;
        [Route("x")] public class XController : ControllerBase
        {
            [HttpGet("one")] public OrderDto One() => null!;
        }
        """;

    [Fact]
    public void AmbiguousTypeName_IsLowConfidence_WithOneDiagnostic()
    {
        var s1 = Src.SetFor("r", "S1", ("r/S1/O.cs", "namespace A.One; public class OrderDto { public int X {get;set;} }"));
        var s2 = Src.SetFor("r", "S2", ("r/S2/O.cs", "namespace A.Two; public class OrderDto { public int Y {get;set;} }"));
        var g = RunSets(6, [Ctl], s1, s2);
        var ret = Assert.Single(g.EdgesOf(EdgeKind.Returns));
        Assert.Equal(Confidence.Low, ret.Confidence);
        Assert.Equal("model:S1:A.One.OrderDto", ret.To);
        var d = Assert.Single(g.Diagnostics, x => x.Kind == DiagnosticKinds.AmbiguousModel);
        Assert.Equal(["model:S1:A.One.OrderDto", "model:S2:A.Two.OrderDto"], d.NodeIds);
    }

    [Fact]
    public void PartialClasses_AreMerged()
    {
        var s1 = Src.SetFor("r", "S1",
            ("r/S1/B.cs", "namespace A; public partial class OrderDto { public int B {get;set;} }"),
            ("r/S1/A.cs", "namespace A; public partial class OrderDto { public int A {get;set;} }"));
        var g = RunSets(6, [Ctl], s1);
        var m = Assert.Single(g.Models, x => x.FullName == "A.OrderDto");
        Assert.Equal(["A", "B"], m.Fields.Select(f => f.Name));
        Assert.Equal("r/S1/A.cs", m.Location!.Path);
    }

    [Fact]
    public void Generics_TypeParametersAreNotModels_AndArityDisambiguates()
    {
        var s1 = Src.SetFor("r", "S1", ("r/S1/R.cs", """
            namespace A;
            public class Result { public int Z {get;set;} }
            public class Result<T> { public T Data {get;set;} public List<T> Items {get;set;} }
            public class OrderDto { public Result<int> Typed {get;set;} public Result Plain {get;set;} }
            """));
        var g = RunSets(6, [Ctl], s1);
        Assert.DoesNotContain(g.Models, m => m.Id == "model:?:T");
        Assert.Contains(g.Models, m => m.Id == "model:S1:A.Result");
        Assert.Contains(g.Models, m => m.Id == "model:S1:A.Result`1");
        Assert.Empty(g.Diagnostics);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void DepthCutoff_DoesNotDependOnTraversalOrder(bool deepFirst)
    {
        // Deep -> Mid -> Leaf -> Tail. maxDepth 3 cuts Leaf off when reached via Deep (depth 3);
        // Leaf reached directly from an endpoint (depth 1) must still expand.
        var ctl = """
            namespace A;
            [Route("x")] public class XController : ControllerBase
            {
            """
            + (deepFirst
                ? "[HttpGet(\"a\")] public Deep A() => null!; [HttpGet(\"b\")] public Leaf B() => null!;"
                : "[HttpGet(\"b\")] public Leaf B() => null!; [HttpGet(\"a\")] public Deep A() => null!;")
            + "}";
        var s1 = Src.SetFor("r", "S1", ("r/S1/M.cs", """
            namespace A;
            public class Deep { public Mid M {get;set;} }
            public class Mid { public Leaf L {get;set;} }
            public class Leaf { public Tail T {get;set;} }
            public class Tail { public int V {get;set;} }
            """));
        var g = RunSets(3, [ctl], s1);
        Assert.Contains(g.EdgesOf(EdgeKind.FieldOf), e => e.From == "model:S1:A.Leaf" && e.To == "model:S1:A.Tail");
    }

    [Fact]
    public void Resolution_IsIndependentOfSourceOrder()
    {
        var s1 = Src.SetFor("r", "S1", ("r/S1/O.cs", "namespace A.One; public class OrderDto { }"));
        var s2 = Src.SetFor("r", "S2", ("r/S2/O.cs", "namespace A.Two; public class OrderDto { }"));
        var a = new TypeIndex([s1, s2]).Resolve("OrderDto", "proj:r/Api", []);
        var b = new TypeIndex([s2, s1]).Resolve("OrderDto", "proj:r/Api", []);
        Assert.Equal("A.One.OrderDto", a!.FullName);
        Assert.Equal("A.One.OrderDto", b!.FullName);
    }

    [Fact]
    public void DottedBase_AndPositionalPlusExplicitProperty()
    {
        var s1 = Src.SetFor("r", "S1", ("r/S1/O.cs", """
            namespace A;
            public class EntityBase { public int Id {get;set;} }
            public class OrderDto : Ns.EntityBase { public int Own {get;set;} }
            """), ("r/S1/P.cs", "namespace A; public record Pos(int Q) { public int Q { get; init; } }"));
        var g = RunSets(6, [Ctl], s1);
        Assert.Equal(["Own", "Id"], M(g, "A.OrderDto").Fields.Select(f => f.Name));
        var other = new DepGraph();
        new ModelExtractor([s1], _ => []).Extract(other, new HashSet<string> { "proj:r/S1" });
        Assert.Single(other.Models.Single(m => m.FullName == "A.Pos").Fields);
    }

    [Fact]
    public void ClientTypes_AreNotSeeded_AndPlainClassPrimaryCtorParamsAreNotFields()
    {
        var s = Src.SetFor("r", "S1", ("r/S1/C.cs", """
            namespace A;
            public interface IOrdersClient { Task<OrderDto> Get(); }
            public class OrdersClient(Acme.Http.IApiHttpClient http) : IOrdersClient { }
            public class Holder(int x) { public int Y { get; set; } }
            public struct Pt(int a) { public int B { get; set; } }
            public record Rec(int Q);
            public class OrderDto { public int Id { get; set; } }
            """));
        var g = new DepGraph();
        var types = new HashSet<(string, string)> { ("proj:r/S1", "IOrdersClient"), ("proj:r/S1", "OrdersClient") };
        new ModelExtractor([s], _ => []).Extract(g, new HashSet<string> { "proj:r/S1" }, types);

        var names = g.Models.Select(m => m.FullName).ToList();
        Assert.DoesNotContain("A.IOrdersClient", names);
        Assert.DoesNotContain("A.OrdersClient", names);
        Assert.DoesNotContain(g.Models, m => m.Kind == ModelKind.Opaque);
        Assert.Equal(["Y"], M(g, "A.Holder").Fields.Select(f => f.Name));
        Assert.Equal(["B"], M(g, "A.Pt").Fields.Select(f => f.Name));
        Assert.Equal(["Q"], M(g, "A.Rec").Fields.Select(f => f.Name));
    }
}
