using Acme.Orders.Client;
using Microsoft.AspNetCore.Mvc;

namespace Acme.Orders.Api;

[ApiController]
[Route("api/[controller]")]
public class OrdersController : ControllerBase
{
    [HttpGet("{id}")] public Task<ActionResult<OrderDto>> Get(Guid id) => null!;
    [HttpGet] public Task<ActionResult<List<OrderDto>>> List([FromQuery] int page) => null!;
    [HttpPost] public Task<ActionResult<OrderDto>> Create(CreateOrderRequest request) => null!;
    [HttpDelete("{id}")] public Task<IActionResult> Delete(Guid id) => null!;
}
