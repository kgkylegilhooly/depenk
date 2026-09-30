using Depenk.Analysis.Routes;

namespace Depenk.Tests.Analysis;

public class RouteNormalizerTests
{
    [Theory]
    [InlineData("/api/orders/{id}", "api/orders/{}")]
    [InlineData("api/Orders/{id:int}", "api/orders/{}")]
    [InlineData("api/orders/{orderId}/lines/{lineId?}", "api/orders/{}/lines/{}")]
    [InlineData("api/files/{*path}", "api/files/{}")]
    [InlineData("api/orders?page={p}&size=10", "api/orders")]
    [InlineData("https://orders.internal:5001/api/orders/{id}", "api/orders/{}")]
    [InlineData("~/api//orders/", "api/orders")]
    [InlineData("  /api/orders#frag ", "api/orders")]
    [InlineData("api/orders/{id}.json", "api/orders/{}.json")]
    [InlineData("", "")]
    public void Normalize(string input, string expected) => Assert.Equal(expected, RouteNormalizer.Normalize(input));

    [Theory]
    [InlineData("api/orders", "{id}", "/api/orders/{id}")]
    [InlineData("api/orders", "/health", "/health")]
    [InlineData("api/orders", "~/v2/orders", "/v2/orders")]
    [InlineData(null, "api/x", "/api/x")]
    [InlineData("api/orders/", null, "/api/orders")]
    [InlineData(null, null, "/")]
    public void Combine(string? prefix, string? template, string expected) =>
        Assert.Equal(expected, RouteNormalizer.Combine(prefix, template));

    [Fact]
    public void ReplacesControllerAndActionTokens()
    {
        Assert.Equal("api/orders/list", RouteNormalizer.ReplaceTokens("api/[controller]/[action]", "OrdersController", "List"));
        Assert.Equal("api/orders", RouteNormalizer.ReplaceTokens("api/[Controller]", "Orders", "Get"));
    }
}
