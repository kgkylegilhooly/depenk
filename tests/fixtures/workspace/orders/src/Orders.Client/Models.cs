using Acme.Customers.Client;
using Acme.Shared;

namespace Acme.Orders.Client;

public class OrderDto
{
    public Guid Id { get; init; }
    public OrderStatus Status { get; init; }
    public CustomerDto? Customer { get; init; }
    public List<OrderLineDto> Lines { get; init; } = [];
}

public class OrderLineDto
{
    public string Sku { get; set; } = "";
    public int Qty { get; set; }
    public Money UnitPrice { get; set; } = new(0, "USD");
}

public enum OrderStatus { Pending, Paid, Shipped }

public record CreateOrderRequest(Guid CustomerId, List<OrderLineDto> Lines);
