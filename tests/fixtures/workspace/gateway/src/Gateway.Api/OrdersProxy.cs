using Acme.Orders.Client;

namespace Acme.Gateway.Api;

public class OrdersProxy
{
    private readonly IOrdersClient _orders;
    public OrdersProxy(IOrdersClient orders) => _orders = orders;
    public Task<List<OrderDto>> List(int page) => _orders.ListAsync(page);
    public Task<OrderDto> Create(CreateOrderRequest r) => _orders.CreateAsync(r);
}
