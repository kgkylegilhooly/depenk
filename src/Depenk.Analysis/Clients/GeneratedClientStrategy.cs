using System.Text.RegularExpressions;
using Depenk.Core.Model;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using static Depenk.Analysis.SyntaxHelpers;

namespace Depenk.Analysis.Clients;

public sealed partial class GeneratedClientStrategy : IRouteStrategy
{
    public RouteHit? Match(MethodDeclarationSyntax method, TypeDeclarationSyntax owner) =>
        MatchNSwag(method) ?? MatchKiota(method, owner);

    [GeneratedRegex(@"//\s*Operation Path:\s*""([^""]*)""")]
    private static partial Regex OperationPath();

    private static bool MentionsBaseUrl(ExpressionSyntax e) =>
        e.DescendantNodesAndSelf().OfType<IdentifierNameSyntax>()
            .Any(i => i.Identifier.Text.Contains("baseurl", StringComparison.OrdinalIgnoreCase));

    private static bool RootsAtUrlBuilder(ExpressionSyntax e) => e switch
    {
        IdentifierNameSyntax id => id.Identifier.Text == "urlBuilder_",
        InvocationExpressionSyntax { Expression: MemberAccessExpressionSyntax m } => RootsAtUrlBuilder(m.Expression),
        _ => false,
    };

    private static RouteHit? MatchNSwag(MethodDeclarationSyntax method)
    {
        var verb = method.DescendantNodes().OfType<AssignmentExpressionSyntax>()
            .Where(a => a.Left.ToString().EndsWith(".Method", StringComparison.Ordinal))
            .Select(a => Verbs.FromHttpMethodExpression(a.Right)).FirstOrDefault(v => v is not null);
        if (verb is null) return null;

        if (method.Body is { } body)
            foreach (var t in body.DescendantTrivia())
                if (t.IsKind(SyntaxKind.SingleLineCommentTrivia) && OperationPath().Match(t.ToString()) is { Success: true } om)
                    return new RouteHit(verb, om.Groups[1].Value, "generated", Confidence.High);

        var appends = method.DescendantNodes().OfType<InvocationExpressionSyntax>()
            .Where(i => i.Expression is MemberAccessExpressionSyntax { Name.Identifier.Text: "Append" } m
                        && RootsAtUrlBuilder(m.Expression) && i.ArgumentList.Arguments.Count == 1)
            .OrderBy(i => i.ArgumentList.SpanStart)
            .Select(i => i.ArgumentList.Arguments[0].Expression)
            .Where(e => !MentionsBaseUrl(e))
            .Select(e => StringValue(e) ?? "{}")
            .ToList();
        return appends.Count == 0 ? null : new RouteHit(verb, string.Concat(appends), "generated", Confidence.High);
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
