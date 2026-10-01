using Depenk.Analysis.Models;

namespace Depenk.Tests.Analysis;

public class TypeUnwrapperTests
{
    [Theory]
    [InlineData("Task<ActionResult<List<OrderDto>>>", "OrderDto*")]
    [InlineData("OrderDto?", "OrderDto")]
    [InlineData("Acme.Orders.OrderDto[]", "OrderDto*")]
    [InlineData("Results<Ok<OrderDto>, NotFound>", "OrderDto")]
    [InlineData("PagedResult<OrderDto>", "PagedResult,OrderDto")]
    [InlineData("Dictionary<string, OrderLineDto>", "OrderLineDto*")]
    [InlineData("Task<IActionResult>", "")]
    [InlineData("Guid", "")]
    [InlineData("int?", "")]
    [InlineData("IAsyncEnumerable<OrderDto>", "OrderDto")]
    public void Unwraps(string input, string expected)
    {
        var actual = string.Join(",", TypeUnwrapper.Unwrap(input).Select(r => r.Name + (r.Collection ? "*" : "")));
        Assert.Equal(expected, actual);
    }
}
