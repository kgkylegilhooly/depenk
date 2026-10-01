using Acme.Billing.Client;

namespace Acme.Orders.Api;

public class InvoiceNotifier(IBillingApi billing)
{
    public Task NotifyAsync(Guid orderId) => billing.CreateInvoice(new CreateInvoiceRequest(orderId));
}
