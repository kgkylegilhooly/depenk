using Depenk.Core.Model;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Depenk.Analysis.Clients;

public sealed class GenericHttpStrategy : IRouteStrategy
{
    private static readonly HashSet<string> IgnoredReceivers = ["Path", "File", "Directory", "Environment"];
    private static readonly HashSet<string> IgnoredNames = ["GetSection", "GetValue", "GetConnectionString"];

    public RouteHit? Match(MethodDeclarationSyntax method, TypeDeclarationSyntax owner)
    {
        foreach (var node in method.DescendantNodes())
        {
            if (node is ObjectCreationExpressionSyntax oc && oc.Type.ToString().EndsWith("HttpRequestMessage", StringComparison.Ordinal)
                && oc.ArgumentList?.Arguments.Count >= 2
                && Verbs.FromHttpMethodExpression(oc.ArgumentList.Arguments[0].Expression) is { } v
                && Verbs.RouteValue(oc.ArgumentList.Arguments[1].Expression, method) is { } r && Verbs.IsRouteLike(r))
                return new RouteHit(v, r, "generic-http", Confidence.Medium);

            if (node is InvocationExpressionSyntax inv)
            {
                var hasReceiver = inv.Expression is MemberAccessExpressionSyntax;
                var name = inv.Expression switch
                {
                    MemberAccessExpressionSyntax m => m.Name.Identifier.Text,
                    IdentifierNameSyntax id => id.Identifier.Text,
                    GenericNameSyntax g => g.Identifier.Text,
                    _ => null,
                };
                if (name is null || Verbs.FromMethodName(name) is not { } verb) continue;
                if (IgnoredNames.Contains(name)) continue;
                if (inv.Expression is MemberAccessExpressionSyntax ma
                    && IgnoredReceivers.Contains(ma.Expression.ToString().Split('.')[^1])) continue;
                if (!hasReceiver && name == method.Identifier.Text) continue; // self/overload call, handled by delegation pass
                var route = inv.ArgumentList.Arguments.Select(a => Verbs.RouteValue(a.Expression, method))
                    .FirstOrDefault(Verbs.IsRouteLike);
                if (route is not null) return new RouteHit(verb, route, "generic-http", Confidence.Medium);
            }
        }
        return null;
    }
}
