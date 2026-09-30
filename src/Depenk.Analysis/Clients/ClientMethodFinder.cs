using Depenk.Analysis.Routes;
using Depenk.Core;
using Depenk.Core.Model;
using Depenk.Scanning.Config;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using static Depenk.Analysis.SyntaxHelpers;

namespace Depenk.Analysis.Clients;

public sealed record ClientScanResult(List<ClientMethodNode> Methods, bool AnyHit);

public sealed class ClientMethodFinder(DepenkConfig config)
{
    private readonly IRouteStrategy[] _strategies =
    [
        new RefitStrategy(), new GeneratedClientStrategy(),
        new ConfiguredWrapperStrategy(config.HttpWrappers), new GenericHttpStrategy(),
    ];

    private sealed record Candidate(SourceDoc Doc, TypeDeclarationSyntax Owner, MethodDeclarationSyntax Method)
    {
        public RouteHit? Hit { get; set; }
    }

    public ClientScanResult Find(SourceSet src)
    {
        var candidates = src.All<TypeDeclarationSyntax>()
            .Where(t => t.Node is ClassDeclarationSyntax or InterfaceDeclarationSyntax)
            .SelectMany(t => t.Node.Members.OfType<MethodDeclarationSyntax>()
                .Where(m => t.Node is InterfaceDeclarationSyntax
                            || (m.Modifiers.Any(SyntaxKind.PublicKeyword) && !m.Modifiers.Any(SyntaxKind.StaticKeyword)))
                .Select(m => new Candidate(t.Doc, t.Node, m)))
            .ToList();

        foreach (var c in candidates)
            c.Hit = _strategies.Select(s => s.Match(c.Method, c.Owner)).FirstOrDefault(h => h is not null);

        // pass 2a: overload/self delegation inside the same class
        foreach (var c in candidates.Where(c => c.Hit is null && c.Owner is ClassDeclarationSyntax))
        {
            var calls = c.Method.DescendantNodes().OfType<InvocationExpressionSyntax>()
                .Select(i => i.Expression switch { IdentifierNameSyntax id => id.Identifier.Text, _ => null })
                .Where(n => n == c.Method.Identifier.Text);
            if (calls.Any())
                c.Hit = candidates.FirstOrDefault(o => o != c && o.Owner == c.Owner && o.Hit is not null
                                                        && o.Method.Identifier.Text == c.Method.Identifier.Text)?.Hit;
        }

        // pass 2b: interface methods inherit from implementing classes
        foreach (var c in candidates.Where(c => c.Hit is null && c.Owner is InterfaceDeclarationSyntax))
        {
            var iface = c.Owner.Identifier.Text;
            c.Hit = candidates.FirstOrDefault(o => o.Hit is not null && o.Owner is ClassDeclarationSyntax
                                                   && DeclaredTypes.BaseTypes(o.Owner).Any(b => b.Split('<')[0] == iface)
                                                   && o.Method.Identifier.Text == c.Method.Identifier.Text)?.Hit;
        }

        var ownersWithHits = candidates.Where(c => c.Hit is not null).Select(c => c.Owner).ToHashSet();
        var prefix = config.Routes.Prefixes.GetValueOrDefault(src.ProjectName);
        var nodes = new Dictionary<string, ClientMethodNode>();
        foreach (var c in candidates)
        {
            if (c.Hit is null && !(ownersWithHits.Contains(c.Owner) && IsAsync(c.Method))) continue;
            var typeName = c.Owner.Identifier.Text;
            var id = Ids.ClientMethod(src.ProjectName, typeName, c.Method.Identifier.Text);
            if (nodes.TryGetValue(id, out var existing) && (existing.Verb is not null || c.Hit is null)) continue;

            var route = c.Hit is null ? null
                : prefix is null ? RouteNormalizer.Combine(null, c.Hit.Route)
                : RouteNormalizer.Combine(prefix, c.Hit.Route.TrimStart('/'));
            nodes[id] = new ClientMethodNode(id, src.Repo, src.ProjectId, typeName, c.Method.Identifier.Text,
                $"{TypeName(c.Method.ReturnType)} {c.Method.Identifier.Text}({c.Method.ParameterList.Parameters})",
                c.Hit?.Verb, route, route is null ? null : RouteNormalizer.Normalize(route),
                c.Hit?.Strategy ?? "none", c.Hit?.Confidence ?? Confidence.Low,
                new SourceLocation(c.Doc.RelativePath, Line(c.Method.Identifier)));
        }
        return new ClientScanResult([.. nodes.Values], candidates.Any(c => c.Hit is not null));
    }

    private static bool IsAsync(MethodDeclarationSyntax m)
    {
        var rt = TypeName(m.ReturnType);
        return rt is "Task" or "ValueTask" || rt.StartsWith("Task<", StringComparison.Ordinal) || rt.StartsWith("ValueTask<", StringComparison.Ordinal);
    }
}
