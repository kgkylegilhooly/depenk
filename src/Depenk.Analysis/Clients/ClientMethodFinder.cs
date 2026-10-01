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
            var impls = candidates.Where(o => o.Hit is not null && o.Owner is ClassDeclarationSyntax
                                              && DeclaredTypes.BaseTypes(o.Owner).Any(b => b.Split('<')[0] == iface)
                                              && o.Method.Identifier.Text == c.Method.Identifier.Text).ToList();
            // overloads: prefer the implementation with the same parameter count
            c.Hit = (impls.FirstOrDefault(o => o.Method.ParameterList.Parameters.Count == c.Method.ParameterList.Parameters.Count)
                     ?? impls.FirstOrDefault())?.Hit;
        }

        var ownersWithHits = candidates.Where(c => c.Hit is not null).Select(c => c.Owner).ToHashSet();
        var prefix = config.Routes.Prefixes.GetValueOrDefault(src.ProjectName);
        // Overloads share a base id. Overloads that agree on (verb, normalized route) collapse into one node; each
        // distinct (verb, route) keeps its own node: the first holds the base id, later ones get "#2", "#3", ...
        var nodes = new List<ClientMethodNode>();
        var byBaseId = new Dictionary<string, List<int>>(StringComparer.Ordinal);
        foreach (var c in candidates)
        {
            if (c.Hit is null && !(ownersWithHits.Contains(c.Owner) && IsAsync(c.Method))) continue;
            var typeName = c.Owner.Identifier.Text;
            var baseId = Ids.ClientMethod(src.ProjectName, typeName, c.Method.Identifier.Text);
            if (!byBaseId.TryGetValue(baseId, out var group)) byBaseId[baseId] = group = [];

            var route = c.Hit is null ? null
                : prefix is null ? RouteNormalizer.Combine(null, c.Hit.Route)
                : RouteNormalizer.Combine(prefix, c.Hit.Route.TrimStart('/'));
            var normalized = route is null ? null : RouteNormalizer.Normalize(route);

            int? replace = null;
            if (c.Hit is null)
            {
                if (group.Count > 0) continue; // a verbless overload adds nothing to an existing node
            }
            else
            {
                if (group.Any(i => string.Equals(nodes[i].Verb, c.Hit.Verb, StringComparison.OrdinalIgnoreCase)
                                   && nodes[i].NormalizedRoute == normalized)) continue;
                var verbless = group.FindIndex(i => nodes[i].Verb is null);
                if (verbless >= 0) replace = group[verbless];
            }

            var id = replace is { } r ? nodes[r].Id : group.Count == 0 ? baseId : $"{baseId}#{group.Count + 1}";
            var node = new ClientMethodNode(id, src.Repo, src.ProjectId, typeName, c.Method.Identifier.Text,
                $"{TypeName(c.Method.ReturnType)} {c.Method.Identifier.Text}({c.Method.ParameterList.Parameters})",
                c.Hit?.Verb, route, normalized,
                c.Hit?.Strategy ?? "none", c.Hit?.Confidence ?? Confidence.Low,
                new SourceLocation(c.Doc.RelativePath, Line(c.Method.Identifier)));
            if (replace is { } at) { nodes[at] = node; continue; }
            group.Add(nodes.Count);
            nodes.Add(node);
        }
        return new ClientScanResult(nodes, candidates.Any(c => c.Hit is not null));
    }

    private static bool IsAsync(MethodDeclarationSyntax m)
    {
        var rt = TypeName(m.ReturnType);
        return rt is "Task" or "ValueTask" || rt.StartsWith("Task<", StringComparison.Ordinal) || rt.StartsWith("ValueTask<", StringComparison.Ordinal);
    }
}
