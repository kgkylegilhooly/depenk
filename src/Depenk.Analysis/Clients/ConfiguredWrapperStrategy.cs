using Depenk.Core.Model;
using Depenk.Scanning.Config;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Depenk.Analysis.Clients;

public sealed class ConfiguredWrapperStrategy(IReadOnlyList<HttpWrapperConfig> wrappers) : IRouteStrategy
{
    public RouteHit? Match(MethodDeclarationSyntax method, TypeDeclarationSyntax owner)
    {
        if (wrappers.Count == 0) return null;
        foreach (var inv in method.DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            var (receiverTypes, calledName) = inv.Expression switch
            {
                MemberAccessExpressionSyntax m => (DeclaredTypes.Of(m.Expression, inv) is { } t ? [t] : new List<string>(),
                    m.Name.Identifier.Text),
                IdentifierNameSyntax id => (DeclaredTypes.BaseTypes(owner), id.Identifier.Text),
                GenericNameSyntax g => (DeclaredTypes.BaseTypes(owner), g.Identifier.Text),
                _ => (new List<string>(), ""),
            };
            foreach (var w in wrappers)
            {
                if (!receiverTypes.Any(t => Glob.IsMatch(w.Type, StripGenerics(t)))) continue;
                var verb = w.Methods.FirstOrDefault(kv => Glob.IsMatch(kv.Key, calledName)).Value;
                var args = inv.ArgumentList.Arguments;
                if (verb is null || w.RouteArgument >= args.Count) continue;
                if (Verbs.RouteValue(args[w.RouteArgument].Expression, method) is { } route)
                    return new RouteHit(verb.ToUpperInvariant(), route, "configured-wrapper", Confidence.High);
            }
        }
        return null;
    }

    private static string StripGenerics(string t) => t.Split('<')[0];
}
