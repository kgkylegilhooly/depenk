using Depenk.Analysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Depenk.Tests.Analysis;

public class DeclaredTypesTests
{
    private static string? TypeOfReceiver(string code, string receiverText)
    {
        var root = CSharpSyntaxTree.ParseText(code).GetRoot();
        var inv = root.DescendantNodes().OfType<InvocationExpressionSyntax>()
            .First(i => i.Expression is MemberAccessExpressionSyntax m && m.Expression.ToString() == receiverText);
        return DeclaredTypes.Of(((MemberAccessExpressionSyntax)inv.Expression).Expression, inv);
    }

    [Theory]
    [InlineData("class A { private readonly IOrdersClient _orders; void M() { _orders.Go(); } }", "_orders", "IOrdersClient")]
    [InlineData("class A { IOrdersClient Orders { get; } void M() { this.Orders.Go(); } }", "this.Orders", "IOrdersClient")]
    [InlineData("class A { void M(IOrdersClient c) { c.Go(); } }", "c", "IOrdersClient")]
    [InlineData("class A(IOrdersClient orders) { void M() { orders.Go(); } }", "orders", "IOrdersClient")]
    [InlineData("class A { void M() { var c = new OrdersClient(null); c.Go(); } }", "c", "OrdersClient")]
    [InlineData("class A { void M(IServiceProvider sp) { var c = sp.GetRequiredService<IOrdersClient>(); c.Go(); } }", "c", "IOrdersClient")]
    [InlineData("class A { void M() { IOrdersClient c = Make(); c.Go(); } }", "c", "IOrdersClient")]
    [InlineData("class A { void M() { var c = Make(); c.Go(); } }", "c", null)]
    public void ResolvesDeclaredType(string code, string receiver, string? expected) =>
        Assert.Equal(expected, TypeOfReceiver(code, receiver));
}
