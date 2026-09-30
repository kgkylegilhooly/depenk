using System.Text.RegularExpressions;
using Depenk.Core.Model;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using static Depenk.Analysis.SyntaxHelpers;

namespace Depenk.Analysis.Clients;

public sealed partial class GeneratedClientStrategy : IRouteStrategy
{
    public RouteHit? Match(MethodDeclarationSyntax method, TypeDeclarationSyntax owner) =>
        MatchNSwag(method) ?? MatchKiota(method, owner);

    private static RouteHit? MatchNSwag(MethodDeclarationSyntax method)
    {
        var appends = method.DescendantNodes().OfType<InvocationExpressionSyntax>()
            .Where(i => i.Expression is MemberAccessExpressionSyntax { Name.Identifier.Text: "Append" } m
                        && m.Expression.ToString() == "urlBuilder_" && i.ArgumentList.Arguments.Count == 1)
            .Select(i => StringValue(i.ArgumentList.Arguments[0].Expression) ?? "{}")
            .ToList();
        if (appends.Count == 0) return null;

        var verb = method.DescendantNodes().OfType<AssignmentExpressionSyntax>()
            .Where(a => a.Left.ToString().EndsWith(".Method", StringComparison.Ordinal))
            .Select(a => Verbs.FromHttpMethodExpression(a.Right)).FirstOrDefault(v => v is not null);
        return verb is null ? null : new RouteHit(verb, string.Concat(appends), "generated", Confidence.High);
    }

    private static RouteHit? MatchKiota(MethodDeclarationSyntax method, TypeDeclarationSyntax owner)
    {
        var verb = Verbs.FromMethodName(method.Identifier.Text);
        if (verb is null) return null;
        var template = owner.Members.OfType<ConstructorDeclarationSyntax>()
            .Select(c => c.Initializer?.ArgumentList.Arguments.Select(a => StringValue(a.Expression))
                .FirstOrDefault(s => s?.Contains("{+baseurl}") == true))
            .FirstOrDefault(s => s is not null);
        if (template is null) return null;
        var route = KiotaQuery().Replace(template.Replace("{+baseurl}", ""), "");
        return new RouteHit(verb, route, "generated", Confidence.High);
    }

    [GeneratedRegex(@"\{\?[^}]*\}")]
    private static partial Regex KiotaQuery();
}
