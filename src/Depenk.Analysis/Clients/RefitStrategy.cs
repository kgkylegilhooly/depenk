using Depenk.Core.Model;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using static Depenk.Analysis.SyntaxHelpers;

namespace Depenk.Analysis.Clients;

public sealed class RefitStrategy : IRouteStrategy
{
    private static readonly HashSet<string> VerbAttrs = ["Get", "Post", "Put", "Delete", "Patch", "Head"];

    public RouteHit? Match(MethodDeclarationSyntax method, TypeDeclarationSyntax owner) =>
        Attrs(method).Where(a => VerbAttrs.Contains(AttrName(a)))
            .Select(a => FirstStringArg(a) is { } route ? new RouteHit(AttrName(a).ToUpperInvariant(), route, "refit", Confidence.High) : null)
            .FirstOrDefault(h => h is not null);
}
