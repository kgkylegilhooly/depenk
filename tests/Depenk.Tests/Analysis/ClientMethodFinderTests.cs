using Depenk.Analysis.Clients;
using Depenk.Core.Model;
using Depenk.Scanning.Config;
using Depenk.Tests.TestUtil;

namespace Depenk.Tests.Analysis;

public class ClientMethodFinderTests
{
    private static Dictionary<string, ClientMethodNode> Find(string code, DepenkConfig? cfg = null) =>
        new ClientMethodFinder(cfg ?? new DepenkConfig())
            .Find(Src.SetFor("orders", "Orders.Client", ("orders/src/Orders.Client/Client.cs", code)))
            .Methods.ToDictionary(m => $"{m.TypeName}.{m.MethodName}");

    [Fact]
    public void Refit()
    {
        var m = Find("""
            public interface IOrdersApi
            {
                [Get("/api/orders/{id}")] Task<OrderDto> GetOrder(Guid id);
                [Post("/api/orders")] Task<OrderDto> Create([Body] CreateOrderRequest r);
            }
            """);
        Assert.Equal(("GET", "/api/orders/{id}", "api/orders/{}", "refit", Confidence.High),
            (m["IOrdersApi.GetOrder"].Verb, m["IOrdersApi.GetOrder"].Route, m["IOrdersApi.GetOrder"].NormalizedRoute,
             m["IOrdersApi.GetOrder"].Strategy, m["IOrdersApi.GetOrder"].Confidence));
        Assert.Equal("cm:Orders.Client:IOrdersApi.GetOrder", m["IOrdersApi.GetOrder"].Id);
        Assert.Equal("POST", m["IOrdersApi.Create"].Verb);
        Assert.Equal("Task<OrderDto> GetOrder(Guid id)", m["IOrdersApi.GetOrder"].Signature);
    }

    [Fact]
    public void Generic_HttpClient_Interpolated_LocalVariable_AndRequestMessage_WithInterfaceMapping()
    {
        var m = Find("""
            public interface IOrdersClient
            {
                Task<OrderDto> GetOrderAsync(Guid id);
                Task<List<OrderDto>> ListAsync(int page);
                Task SetStatusAsync(Guid id, OrderStatus s);
                Task<Stats> StatsAsync();
            }
            public class OrdersClient(HttpClient http) : IOrdersClient
            {
                public Task<OrderDto> GetOrderAsync(Guid id) => http.GetFromJsonAsync<OrderDto>($"api/orders/{id}")!;
                public Task<List<OrderDto>> ListAsync(int page)
                {
                    var url = "api/orders?page=" + page;
                    return http.GetFromJsonAsync<List<OrderDto>>(url)!;
                }
                public async Task SetStatusAsync(Guid id, OrderStatus s)
                {
                    using var req = new HttpRequestMessage(HttpMethod.Put, $"api/orders/{id}/status");
                    req.Content = new StringContent("", null, "application/json");
                    await http.SendAsync(req);
                }
                public Task<Stats> StatsAsync() => Compute();
                private Task<Stats> Compute() => null!;
            }
            """);
        Assert.Equal(("GET", "/api/orders/{id}", "generic-http", Confidence.Medium),
            (m["OrdersClient.GetOrderAsync"].Verb, m["OrdersClient.GetOrderAsync"].Route,
             m["OrdersClient.GetOrderAsync"].Strategy, m["OrdersClient.GetOrderAsync"].Confidence));
        Assert.Equal("api/orders", m["OrdersClient.ListAsync"].NormalizedRoute);
        Assert.Equal(("PUT", "api/orders/{}/status"), (m["OrdersClient.SetStatusAsync"].Verb, m["OrdersClient.SetStatusAsync"].NormalizedRoute));
        // interface methods inherit the implementation's hit
        Assert.Equal("api/orders/{}", m["IOrdersClient.GetOrderAsync"].NormalizedRoute);
        // unresolved async method is still emitted, low confidence
        Assert.Equal((null, "none", Confidence.Low), (m["OrdersClient.StatsAsync"].Verb, m["OrdersClient.StatsAsync"].Strategy, m["OrdersClient.StatsAsync"].Confidence));
        Assert.False(m.ContainsKey("OrdersClient.Compute"));
    }

    [Fact]
    public void ConfiguredWrapper_ByFieldType_AndByBaseClass()
    {
        var cfg = new DepenkConfig();
        cfg.HttpWrappers.Add(new HttpWrapperConfig
        {
            Type = "*.IApiHttpClient", RouteArgument = 0,
            Methods = new() { ["Fetch*"] = "GET", ["Send*"] = "POST" },
        });
        cfg.HttpWrappers.Add(new HttpWrapperConfig
        {
            Type = "ApiClientBase", RouteArgument = 1, Methods = new() { ["Remove"] = "DELETE" },
        });
        var m = Find("""
            public class OrdersClient
            {
                private readonly Acme.Http.IApiHttpClient _api;
                public Task<OrderDto> Get(Guid id) => _api.FetchAsync<OrderDto>($"orders/{id}");
                public Task Create(CreateOrderRequest r) => _api.SendAsync("orders", r);
            }
            public class RefundsClient : ApiClientBase
            {
                public Task Delete(Guid id) => Remove(CancellationToken.None, $"refunds/{id}");
            }
            """, cfg);
        Assert.Equal(("GET", "orders/{}", "configured-wrapper", Confidence.High),
            (m["OrdersClient.Get"].Verb, m["OrdersClient.Get"].NormalizedRoute, m["OrdersClient.Get"].Strategy, m["OrdersClient.Get"].Confidence));
        Assert.Equal("POST", m["OrdersClient.Create"].Verb);
        Assert.Equal(("DELETE", "refunds/{}"), (m["RefundsClient.Delete"].Verb, m["RefundsClient.Delete"].NormalizedRoute));
    }

    [Fact]
    public void NSwagGenerated_WithOverloadDelegation()
    {
        var m = Find("""
            public partial class OrdersClient
            {
                public virtual Task<OrderDto> GetOrderAsync(Guid id) => GetOrderAsync(id, CancellationToken.None);
                public virtual async Task<OrderDto> GetOrderAsync(Guid id, CancellationToken cancellationToken)
                {
                    var urlBuilder_ = new System.Text.StringBuilder();
                    urlBuilder_.Append("api/orders/");
                    urlBuilder_.Append(Uri.EscapeDataString(ConvertToString(id)));
                    using var request_ = new HttpRequestMessage();
                    request_.Method = new System.Net.Http.HttpMethod("GET");
                    return null!;
                }
            }
            """);
        var hit = m["OrdersClient.GetOrderAsync"];
        Assert.Equal(("GET", "api/orders/{}", "generated", Confidence.High), (hit.Verb, hit.NormalizedRoute, hit.Strategy, hit.Confidence));
    }

    [Fact]
    public void KiotaGenerated()
    {
        var m = Find("""
            public class OrdersItemRequestBuilder : BaseRequestBuilder
            {
                public OrdersItemRequestBuilder(IRequestAdapter requestAdapter) : base(requestAdapter, "{+baseurl}/api/orders/{id}{?includeLines}", null) { }
                public async Task<OrderDto?> GetAsync(CancellationToken ct = default) => null;
                public async Task DeleteAsync(CancellationToken ct = default) { }
            }
            """);
        Assert.Equal(("GET", "api/orders/{}"), (m["OrdersItemRequestBuilder.GetAsync"].Verb, m["OrdersItemRequestBuilder.GetAsync"].NormalizedRoute));
        Assert.Equal("DELETE", m["OrdersItemRequestBuilder.DeleteAsync"].Verb);
    }

    [Fact]
    public void ConfiguredPrefix_IsApplied_AndMediaTypesAreNotRoutes()
    {
        var cfg = new DepenkConfig();
        cfg.Routes.Prefixes["Orders.Client"] = "/api";
        var m = Find("""
            public class OrdersClient(HttpClient http)
            {
                public Task<HttpResponseMessage> Post(string body) => http.PostAsync("/orders", new StringContent(body, null, "application/json"));
                public Task<HttpResponseMessage> Send(string body) => http.PostRaw("application/json", "/orders/x", body);
            }
            """, cfg);
        Assert.Equal("/api/orders", m["OrdersClient.Post"].Route);
        Assert.Equal("/api/orders/x", m["OrdersClient.Send"].Route);
    }

    [Fact]
    public void AnyHit_IsFalse_ForPlainLibraries()
    {
        var result = new ClientMethodFinder(new DepenkConfig()).Find(Src.SetFor("r", "Lib", ("a.cs",
            "public class Maths { public int Add(int a, int b) => a + b; }")));
        Assert.False(result.AnyHit);
        Assert.Empty(result.Methods);
    }

    [Fact]
    public void Generic_SameNameAsMethod_WithReceiver_IsAHit()
    {
        var m = Find("""
            public class OrdersClient(HttpClient http)
            {
                public Task<HttpResponseMessage> GetAsync(Guid id) => http.GetAsync($"api/orders/{id}");
                public Task<HttpResponseMessage> DeleteAsync(Guid id) => http.DeleteAsync($"api/orders/{id}");
            }
            """);
        Assert.Equal("GET", m["OrdersClient.GetAsync"].Verb);
        Assert.Equal("DELETE", m["OrdersClient.DeleteAsync"].Verb);
    }

    [Fact]
    public void NSwag_OlderChainedShape_SkipsBaseUrl()
    {
        var m = Find("""
            public partial class OrdersClient
            {
                public virtual async Task<OrderDto> GetOrderAsync(Guid id)
                {
                    var urlBuilder_ = new System.Text.StringBuilder();
                    urlBuilder_.Append(BaseUrl != null ? BaseUrl.TrimEnd('/') : "").Append("/api/orders/{id}");
                    urlBuilder_.Replace("{id}", System.Uri.EscapeDataString(ConvertToString(id)));
                    request_.Method = new System.Net.Http.HttpMethod("GET");
                    return null!;
                }
            }
            """);
        Assert.Equal("api/orders/{}", m["OrdersClient.GetOrderAsync"].NormalizedRoute);
    }

    [Fact]
    public void NSwag_NewerShape_UsesOperationPathComment_AndCharLiterals()
    {
        var m = Find("""
            public partial class OrdersClient
            {
                public virtual async Task<OrderDto> GetOrderAsync(Guid id)
                {
                    var urlBuilder_ = new System.Text.StringBuilder();
                    // Operation Path: "api/orders/{id}"
                    urlBuilder_.Append("api/orders/");
                    urlBuilder_.Append(System.Uri.EscapeDataString(ConvertToString(id)));
                    urlBuilder_.Append('?');
                    request_.Method = new System.Net.Http.HttpMethod("GET");
                    return null!;
                }
                public virtual async Task<OrderDto> ListAsync()
                {
                    var urlBuilder_ = new System.Text.StringBuilder();
                    urlBuilder_.Append("api/orders").Append('/').Append("all");
                    request_.Method = new System.Net.Http.HttpMethod("GET");
                    return null!;
                }
            }
            """);
        Assert.Equal("api/orders/{}", m["OrdersClient.GetOrderAsync"].NormalizedRoute);
        Assert.Equal("api/orders/all", m["OrdersClient.ListAsync"].NormalizedRoute);
    }

    [Fact]
    public void Generic_FalsePositives_AreIgnored()
    {
        var m = Find("""
            public class Misc(IConfiguration config)
            {
                public Task<string> A() { var n = Path.GetFileName("a/b"); return null!; }
                public Task<string> B() { var s = config.GetSection("A/B"); return null!; }
                public Task<string> C() { Postpone("x/y"); return null!; }
                public Task<string> D() { Getaway("x/y"); return null!; }
            }
            """);
        Assert.Empty(m);
    }
}
