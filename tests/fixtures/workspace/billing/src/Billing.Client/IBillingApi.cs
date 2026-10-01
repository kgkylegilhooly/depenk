using Refit;

namespace Acme.Billing.Client;

public interface IBillingApi
{
    [Post("/api/invoices")]
    Task<InvoiceDto> CreateInvoice([Body] CreateInvoiceRequest request);
}

public record CreateInvoiceRequest(Guid OrderId);
public record InvoiceDto(Guid Id, decimal Total);
