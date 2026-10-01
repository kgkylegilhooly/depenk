namespace Acme.Customers.Client;

public partial class CustomersClient
{
    public virtual Task<CustomerDto> GetCustomerAsync(string key) => GetCustomerAsync(key, CancellationToken.None);

    public virtual async Task<CustomerDto> GetCustomerAsync(string key, CancellationToken cancellationToken)
    {
        var urlBuilder_ = new System.Text.StringBuilder();
        urlBuilder_.Append("api/customers/");
        urlBuilder_.Append(Uri.EscapeDataString(key));
        using var request_ = new System.Net.Http.HttpRequestMessage();
        request_.Method = new System.Net.Http.HttpMethod("GET");
        return await Task.FromResult(new CustomerDto());
    }
}
