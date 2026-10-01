using Acme.Billing.Client;
using Microsoft.AspNetCore.Mvc;

namespace Acme.Billing.Api;

[ApiController]
[Route("api/invoices")]
public class InvoicesController : ControllerBase
{
    [HttpPost] public Task<ActionResult<InvoiceDto>> Create(CreateInvoiceRequest request) => null!;
}
