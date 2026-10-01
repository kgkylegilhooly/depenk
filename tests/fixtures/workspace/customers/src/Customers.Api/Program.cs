using Acme.Customers.Client;

var app = WebApplication.Create(args);
var customers = app.MapGroup("/api/customers");
customers.MapGet("{id:guid}", (Guid id) => TypedResults.Ok(new CustomerDto()));
customers.MapGet("{slug}", (string slug) => TypedResults.Ok(new CustomerDto()));
app.Run();
