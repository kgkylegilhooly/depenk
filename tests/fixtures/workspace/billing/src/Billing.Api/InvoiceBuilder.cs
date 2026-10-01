using Acme.Customers.Client;
using Acme.Orders.Client;

namespace Acme.Billing.Api;

public class InvoiceBuilder(IOrdersClient orders, CustomersClient customers)
{
    public async Task<decimal> BuildAsync(Guid orderId)
    {
        var order = await orders.GetOrderAsync(orderId);
        var customer = await customers.GetCustomerAsync(order.Customer!.Id.ToString());
        return order.Lines.Sum(l => l.Qty * l.UnitPrice.Amount);
    }
}
