using Depenk.Analysis.Endpoints;
using Depenk.Core.Model;
using Depenk.Tests.TestUtil;

namespace Depenk.Tests.Analysis;

public class EndpointFinderTests
{
    private const string OrdersController = """
        using Microsoft.AspNetCore.Mvc;
        namespace Acme.Orders.Api;

        [ApiController]
        [Route("api/[controller]")]
        public class OrdersController : ControllerBase
        {
            [HttpGet("{id:guid}")]
            [ProducesResponseType(typeof(OrderDto), 200)]
            [ProducesResponseType(404)]
            public async Task<IActionResult> Get(Guid id, bool includeLines = true, CancellationToken ct = default) => Ok();

            [HttpGet]
            public Task<ActionResult<List<OrderDto>>> List([FromQuery] int page, string? status) => null!;

            [HttpPost]
            public Task<ActionResult<OrderDto>> Create(CreateOrderRequest request, [FromServices] IClock clock) => null!;

            [HttpPut("/v2/orders/{id}/status")]
            public Task SetStatus(Guid id, [FromBody] OrderStatus status) => Task.CompletedTask;

            private void Helper() { }
        }
        """;

    [Fact]
    public void FindsControllerEndpoints_WithRoutesParamsAndResponses()
    {
        var eps = new ControllerEndpointFinder().Find(Src.Set(("orders/src/Orders.Api/OrdersController.cs", OrdersController)))
            .ToDictionary(e => $"{e.Verb} {e.Route}");

        Assert.Equal(["GET /api/orders/{id:guid}", "GET /api/orders", "POST /api/orders", "PUT /v2/orders/{id}/status"], eps.Keys);

        var get = eps["GET /api/orders/{id:guid}"];
        Assert.Equal("ep:orders:GET:/api/orders/{id:guid}", get.Id);
        Assert.Equal("api/orders/{}", get.NormalizedRoute);
        Assert.Equal("OrdersController.Get", get.Handler);
        Assert.Equal(new SourceLocation("orders/src/Orders.Api/OrdersController.cs", 11), get.Location);
        Assert.Equal([new EndpointParameter("id", "route", "Guid", true, null),
                      new EndpointParameter("includeLines", "query", "bool", false, "true")], get.Parameters);
        Assert.Equal([new ResponseType(200, "OrderDto"), new ResponseType(404, "")], get.Responses);

        var list = eps["GET /api/orders"];
        Assert.Equal([new EndpointParameter("page", "query", "int", true, null),
                      new EndpointParameter("status", "query", "string?", false, null)], list.Parameters);
        Assert.Equal([new ResponseType(200, "Task<ActionResult<List<OrderDto>>>")], list.Responses);

        var create = eps["POST /api/orders"];
        Assert.Equal([new EndpointParameter("request", "body", "CreateOrderRequest", true, null)], create.Parameters);

        var put = eps["PUT /v2/orders/{id}/status"];
        Assert.Equal("body", put.Parameters.Single(p => p.Name == "status").Source);
        Assert.Empty(put.Responses);
    }

    [Fact]
    public void IgnoresNonControllerClasses()
    {
        var eps = new ControllerEndpointFinder().Find(Src.Set(("a.cs", """
            public class PlainService { [HttpGet("x")] public void X() {} }
            """)));
        Assert.Empty(eps);
    }

    [Fact]
    public void FindsMinimalApis_WithGroups()
    {
        var eps = new MinimalApiEndpointFinder().Find(Src.Set(("orders/src/Orders.Api/Program.cs", """
            var app = WebApplication.Create();
            var api = app.MapGroup("/api");
            var refunds = api.MapGroup("refunds");
            refunds.MapPost("/", (CreateRefund body) => Results.Ok());
            refunds.MapGet("{id}", (Guid id) => TypedResults.Ok(new RefundDto()));
            app.MapGet("/health", () => "ok");
            app.MapDelete("/api/refunds/{id}", RefundHandlers.Delete);
            """))).ToList();

        Assert.Equal(["POST /api/refunds", "GET /api/refunds/{id}", "GET /health", "DELETE /api/refunds/{id}"],
            eps.Select(e => $"{e.Verb} {e.Route}"));
        Assert.Equal([new EndpointParameter("body", "body", "CreateRefund", true, null)], eps[0].Parameters);
        Assert.Equal("route", eps[1].Parameters.Single().Source);
        Assert.StartsWith("Program.lambda@", eps[0].Handler);
        Assert.Equal("RefundHandlers.Delete", eps[3].Handler);
    }

    [Fact]
    public void ResolvesConstStringRoutes()
    {
        var eps = new ControllerEndpointFinder().Find(Src.Set(("c.cs", """
            [Route(Routes.Base)]
            public class PingController : ControllerBase
            {
                [HttpGet(Routes.Ping)] public string Ping() => "";
            }
            public static class Routes { public const string Base = "api"; public const string Ping = "ping/" + "{n}"; }
            """)));
        Assert.Equal("/api/ping/{n}", eps.Single().Route);
    }

    [Fact]
    public void SymbolicStatusCodes_AreResolved()
    {
        var eps = new ControllerEndpointFinder().Find(Src.Set(("c.cs", """
            public class XController : ControllerBase
            {
                [HttpGet("a")]
                [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
                [ProducesResponseType(StatusCodes.Status201Created)]
                public IActionResult A() => Ok();
            }
            """))).Single();
        Assert.Equal([new ResponseType(404, "ProblemDetails"), new ResponseType(201, "")], eps.Responses);
    }

    [Fact]
    public void FluentWrappedGroup_KeepsPrefix()
    {
        var eps = new MinimalApiEndpointFinder().Find(Src.Set(("Program.cs", """
            var g = app.MapGroup("/api").WithTags("x").RequireAuthorization();
            g.MapGet("items", () => "ok");
            app.MapGroup("v2").WithOpenApi().MapGet("direct", () => "ok");
            """))).ToList();
        Assert.Equal(["/api/items", "/v2/direct"], eps.Select(e => e.Route));
    }

    [Fact]
    public void GroupVariables_ResolveWithinTheirOwnMethod()
    {
        var eps = new MinimalApiEndpointFinder().Find(Src.Set(("Routes.cs", """
            public static class Routes
            {
                public static void A(WebApplication app) { var g = app.MapGroup("/a"); g.MapGet("x", () => "ok"); }
                public static void B(WebApplication app) { var g = app.MapGroup("/b"); g.MapGet("y", () => "ok"); }
                public static void C(RouteGroupBuilder g) { g.MapGet("z", () => "ok"); }
            }
            """))).ToList();
        Assert.Equal(["/a/x", "/b/y", "/z"], eps.Select(e => e.Route));
    }

    [Fact]
    public void NestedLambdaUsesOuterGroupVariable()
    {
        var eps = new MinimalApiEndpointFinder().Find(Src.Set(("Program.cs", """
            var g = app.MapGroup("/outer");
            app.Lifetime(() => { g.MapGet("in", () => "ok"); });
            """))).ToList();
        Assert.Equal("/outer/in", eps.Single().Route);
    }

    [Fact]
    public void DiServices_AreNotParameters()
    {
        var eps = new MinimalApiEndpointFinder().Find(Src.Set(("Program.cs", """
            app.MapPost("/r", (CreateRefund body, IRefundService svc, ILogger<Program> log, IFormFile file) => "ok");
            """))).Single();
        Assert.Equal(["body", "file"], eps.Parameters.Select(p => p.Name));
    }

    [Fact]
    public void ValueTaskReturnTypes_ProduceNoResponse()
    {
        var eps = new ControllerEndpointFinder().Find(Src.Set(("c.cs", """
            public class XController : ControllerBase
            {
                [HttpGet("a")] public ValueTask<IActionResult> A() => default;
                [HttpGet("b")] public ValueTask B() => default;
                [HttpGet("c")] public ValueTask<ActionResult> C() => default;
            }
            """))).ToList();
        Assert.All(eps, e => Assert.Empty(e.Responses));
    }

    [Fact]
    public void CyclicConsts_ReturnNullWithoutThrowing()
    {
        var tree = Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree.ParseText(
            "class K { const string A = B; const string B = A; string F = A; }");
        var expr = tree.GetRoot().DescendantNodes().OfType<Microsoft.CodeAnalysis.CSharp.Syntax.VariableDeclaratorSyntax>()
            .Single(v => v.Identifier.Text == "F").Initializer!.Value;
        Assert.Null(Depenk.Analysis.SyntaxHelpers.StringValue(expr));
    }

    [Fact]
    public void NamedTemplateArgument_IsRead()
    {
        var eps = new ControllerEndpointFinder().Find(Src.Set(("c.cs", """
            public class XController : ControllerBase
            {
                [HttpGet(template: "x")] public string X() => "";
            }
            """)));
        Assert.Equal("/x", eps.Single().Route);
    }

    [Fact]
    public void RouteInheritedFromBaseController_AbstractNonActionAndStaticSkipped()
    {
        var eps = new ControllerEndpointFinder().Find(Src.Set(
            ("orders/src/Orders.Api/Base.cs", """
                using Microsoft.AspNetCore.Mvc;
                namespace Acme;
                [ApiController, Route("api/v1/[controller]")]
                public abstract class ApiControllerBase : ControllerBase
                {
                    [HttpGet("health")] public string Health() => "";
                }
                public class MiddleController : ApiControllerBase { }
                """),
            ("orders/src/Orders.Api/Orders.cs", """
                using Microsoft.AspNetCore.Mvc;
                namespace Acme;
                public class OrdersController : MiddleController
                {
                    [HttpGet("{id}")] public string Get(int id) => "";
                    [NonAction, HttpGet("hidden")] public string Hidden() => "";
                    [HttpGet("static")] public static string Static() => "";
                }
                public class Things : ApiControllerBase
                {
                    [HttpPost] public void Create() { }
                }
                [Route("own")]
                public class OwnController : ApiControllerBase
                {
                    [HttpGet] public string List() => "";
                }
                """)))
            .Select(e => $"{e.Verb} {e.Route} {e.Handler}").Order().ToList();

        Assert.Equal([
            "GET /api/v1/orders/{id} OrdersController.Get",
            "GET /own OwnController.List",
            "POST /api/v1/things Things.Create",
        ], eps);
    }
}
