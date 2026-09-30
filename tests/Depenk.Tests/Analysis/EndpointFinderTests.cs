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
}
