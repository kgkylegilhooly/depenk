using Depenk.Core.Model;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Depenk.Analysis.Clients;

public sealed record RouteHit(string Verb, string Route, string Strategy, Confidence Confidence);

public interface IRouteStrategy
{
    RouteHit? Match(MethodDeclarationSyntax method, TypeDeclarationSyntax owner);
}

internal static class Verbs
{
    private static readonly (string Prefix, string Verb)[] Prefixes =
        [("Get", "GET"), ("Post", "POST"), ("Put", "PUT"), ("Delete", "DELETE"), ("Patch", "PATCH")];

    public static string? FromMethodName(string name) =>
        Prefixes.FirstOrDefault(p => name.StartsWith(p.Prefix, StringComparison.OrdinalIgnoreCase)).Verb;

    /// <summary>HttpMethod.Get / HttpMethod.Put / new HttpMethod("GET").</summary>
    public static string? FromHttpMethodExpression(ExpressionSyntax e) => e switch
    {
        MemberAccessExpressionSyntax m when m.Expression.ToString().EndsWith("HttpMethod", StringComparison.Ordinal) =>
            m.Name.Identifier.Text.ToUpperInvariant(),
        ObjectCreationExpressionSyntax oc when oc.Type.ToString().EndsWith("HttpMethod", StringComparison.Ordinal)
            && oc.ArgumentList?.Arguments.Count == 1 => SyntaxHelpers.StringValue(oc.ArgumentList.Arguments[0].Expression)?.ToUpperInvariant(),
        _ => null,
    };

    private static readonly string[] MediaPrefixes = ["application/", "text/", "multipart/", "image/"];

    public static bool IsRouteLike(string? s) =>
        !string.IsNullOrEmpty(s) && !s.Any(char.IsWhiteSpace) && (s.Contains('/') || s.Contains('{'))
        && !MediaPrefixes.Any(p => s.StartsWith(p, StringComparison.OrdinalIgnoreCase));

    /// <summary>StringValue, plus resolution of a local variable declared in the same method.</summary>
    public static string? RouteValue(ExpressionSyntax e, MethodDeclarationSyntax method)
    {
        if (SyntaxHelpers.StringValue(e) is { } s) return s;
        if (e is not IdentifierNameSyntax id) return null;
        var local = method.DescendantNodes().OfType<VariableDeclaratorSyntax>()
            .FirstOrDefault(v => v.Identifier.Text == id.Identifier.Text);
        return local?.Initializer?.Value is { } init ? SyntaxHelpers.StringValue(init) : null;
    }
}
