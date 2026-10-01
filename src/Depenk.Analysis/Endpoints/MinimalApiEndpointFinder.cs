using Depenk.Analysis.Routes;
using Depenk.Core;
using Depenk.Core.Model;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using static Depenk.Analysis.SyntaxHelpers;

namespace Depenk.Analysis.Endpoints;

public sealed class MinimalApiEndpointFinder : IEndpointFinder
{
    private static readonly Dictionary<string, string> MapMethods = new()
    {
        ["MapGet"] = "GET", ["MapPost"] = "POST", ["MapPut"] = "PUT", ["MapDelete"] = "DELETE", ["MapPatch"] = "PATCH",
    };

    public IEnumerable<EndpointNode> Find(SourceSet src)
    {
        foreach (var (doc, inv) in src.All<InvocationExpressionSyntax>())
        {
            if (inv.Expression is not MemberAccessExpressionSyntax ma
                || !MapMethods.TryGetValue(ma.Name.Identifier.Text, out var verb)) continue;
            var args = inv.ArgumentList.Arguments;
            if (args.Count < 2 || StringValue(args[0].Expression) is not { } template) continue;

            // inside a group, "/" and "/x" are relative to the group prefix
            var prefix = GroupPrefix(ma.Expression, inv);
            var route = RouteNormalizer.Combine(prefix, prefix is null ? template : template.TrimStart('/'));
            var handlerExpr = args[1].Expression;
            var (handler, parameters) = handlerExpr switch
            {
                ParenthesizedLambdaExpressionSyntax l =>
                    ($"{EnclosingTypeName(inv, doc)}.lambda@{Line(l)}", l.ParameterList.Parameters.AsEnumerable()),
                SimpleLambdaExpressionSyntax s => ($"{EnclosingTypeName(inv, doc)}.lambda@{Line(s)}", new[] { s.Parameter }.AsEnumerable()),
                _ => (handlerExpr.ToString(), Enumerable.Empty<ParameterSyntax>()),
            };

            yield return new EndpointNode(
                Ids.Endpoint(src.Repo, verb, route), src.Repo, src.ProjectId, verb, route,
                RouteNormalizer.Normalize(route), handler, EndpointParameters.From(parameters, verb, route),
                LambdaResponses(handlerExpr), new SourceLocation(doc.RelativePath, Line(inv)));
        }
    }

    /// <summary>Resolves the receiver to a MapGroup prefix: chained calls, fluent wrappers, or a local assigned from MapGroup.</summary>
    private static string? GroupPrefix(ExpressionSyntax receiver, SyntaxNode context, int depth = 0)
    {
        if (depth > 10) return null;
        switch (receiver)
        {
            case InvocationExpressionSyntax { Expression: MemberAccessExpressionSyntax { Name.Identifier.Text: "MapGroup" } gm } g:
                var own = g.ArgumentList.Arguments.Count > 0 ? StringValue(g.ArgumentList.Arguments[0].Expression) : null;
                var outer = GroupPrefix(gm.Expression, context, depth + 1);
                return RouteNormalizer.Combine(outer, own is null ? null : own.TrimStart('/')).TrimStart('/');
            case InvocationExpressionSyntax { Expression: MemberAccessExpressionSyntax wrapper }:
                // WithTags(...), RequireAuthorization(), WithOpenApi(), ... keep the same group
                return GroupPrefix(wrapper.Expression, context, depth + 1);
            case IdentifierNameSyntax id:
                var decl = FindLocal(id.Identifier.Text, context);
                return decl?.Initializer?.Value is { } init ? GroupPrefix(init, decl, depth + 1) : null;
            default:
                return null;
        }
    }

    /// <summary>Nearest enclosing scope wins; only declarations before the use site count.</summary>
    private static VariableDeclaratorSyntax? FindLocal(string name, SyntaxNode use)
    {
        foreach (var scope in use.Ancestors().Where(a => a is BlockSyntax or CompilationUnitSyntax))
        {
            var found = scope.DescendantNodes().OfType<VariableDeclaratorSyntax>()
                .Where(v => v.Identifier.Text == name && v.SpanStart < use.SpanStart
                            && v.Ancestors().FirstOrDefault(a => a is BlockSyntax or CompilationUnitSyntax) == scope)
                .LastOrDefault();
            if (found is not null) return found;
        }
        return null;
    }

    private static string EnclosingTypeName(SyntaxNode n, SourceDoc doc) =>
        n.Ancestors().OfType<TypeDeclarationSyntax>().FirstOrDefault()?.Identifier.Text
        ?? Path.GetFileNameWithoutExtension(doc.RelativePath);

    private static List<ResponseType> LambdaResponses(ExpressionSyntax handler) =>
        handler is ParenthesizedLambdaExpressionSyntax { ReturnType: { } rt } ? [new ResponseType(200, TypeName(rt))] : [];
}
