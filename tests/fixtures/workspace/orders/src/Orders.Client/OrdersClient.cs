namespace Acme.Orders.Client;

public interface IOrdersClient
{
    Task<OrderDto> GetOrderAsync(Guid id);
    Task<List<OrderDto>> ListAsync(int page);
    Task<OrderDto> CreateAsync(CreateOrderRequest request);
}

public sealed class OrdersClient(Acme.Http.IApiHttpClient http) : IOrdersClient
{
    public Task<OrderDto> GetOrderAsync(Guid id) => http.GetJsonAsync<OrderDto>($"api/orders/{id}");
    public Task<List<OrderDto>> ListAsync(int page) => http.GetJsonAsync<List<OrderDto>>($"api/orders?page={page}");
    public Task<OrderDto> CreateAsync(CreateOrderRequest request) => http.PostJsonAsync<OrderDto>("api/orders", request);
}
