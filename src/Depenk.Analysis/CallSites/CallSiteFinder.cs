using Depenk.Core;
using Depenk.Core.Model;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using static Depenk.Analysis.SyntaxHelpers;
using Diagnostic = Depenk.Core.Model.Diagnostic;

namespace Depenk.Analysis.CallSites;

public sealed record CallSiteScan(List<CallSiteNode> CallSites, List<Edge> Invokes, List<Diagnostic> Diagnostics);

public static class CallSiteFinder
{
    public static CallSiteScan Find(SourceSet consumer, IReadOnlyList<ClientMethodNode> reachableClientMethods)
    {
        var sites = new Dictionary<string, CallSiteNode>();
        var edges = new List<Edge>();
        var seen = new HashSet<Edge>();
        var diagnostics = new List<Diagnostic>();
        var diagnosed = new HashSet<string>();
        if (reachableClientMethods.Count == 0) return new CallSiteScan([], [], []);
        var byKey = reachableClientMethods.ToLookup(m => (m.TypeName, m.MethodName));

        foreach (var (doc, inv) in consumer.All<InvocationExpressionSyntax>())
        {
            var (receiver, name) = inv.Expression switch
            {
                MemberAccessExpressionSyntax m => (m.Expression, m.Name.Identifier.Text),
                MemberBindingExpressionSyntax b
                    when inv.Ancestors().OfType<ConditionalAccessExpressionSyntax>().FirstOrDefault() is { } ca =>
                    (ca.Expression, b.Name.Identifier.Text),
                _ => ((ExpressionSyntax?)null, ""),
            };
            if (receiver is null) continue;
            if (DeclaredTypes.Of(receiver, inv) is not { } declared) continue;
            var matches = byKey[(Normalize(declared), name)].ToList();
            if (matches.Count == 0) continue;
            if (matches.Count > 1) matches = ByArgumentCount(matches, inv.ArgumentList.Arguments.Count);
            if (matches.Count > 1) matches = Disambiguate(matches, declared, doc);

            var ambiguous = matches.Count > 1;
            var conf = ambiguous ? Confidence.Low : Confidence.Medium;
            var (typeName, member) = Containing(inv);
            var line = Line(inv);
            var id = Ids.CallSite(consumer.Repo, consumer.ProjectName, typeName, member, line);
            if (!sites.TryGetValue(id, out var site))
                sites[id] = new CallSiteNode(id, consumer.Repo, consumer.ProjectId, $"{typeName}.{member}",
                    conf, new SourceLocation(doc.RelativePath, line));
            else if (ambiguous && site.Confidence != Confidence.Low)
                sites[id] = site with { Confidence = Confidence.Low };
            if (ambiguous && diagnosed.Add(id))
                diagnostics.Add(new Diagnostic(DiagnosticKinds.AmbiguousCallSite, Severities.Info, [id, .. matches.Select(m => m.Id)],
                    $"Call to {Normalize(declared)}.{name} could target several client packages: " +
                    string.Join(", ", matches.Select(m => ProjectName(m))) + "."));
            foreach (var cm in matches)
            {
                var edge = new Edge(EdgeKind.Invokes, id, cm.Id, conf);
                if (seen.Add(edge)) edges.Add(edge);
            }
        }
        return new CallSiteScan([.. sites.Values], edges, diagnostics);
    }

    /// <summary>
    /// Same simple type name in several reachable packages: keep the candidates whose project name relates to the
    /// explicit qualifier (e.g. "A.Client.R0Client") or, failing that, to the file's using directives.
    /// Falls back to all candidates when nothing relates.
    /// </summary>
    private static List<ClientMethodNode> Disambiguate(List<ClientMethodNode> matches, string declared, SourceDoc doc)
    {
        var t = declared.TrimEnd('?');
        var lt = t.IndexOf('<');
        if (lt >= 0) t = t[..lt];
        var dot = t.LastIndexOf('.');
        var hints = dot > 0
            ? [t[..dot]]
            : doc.Tree.GetRoot().DescendantNodes().OfType<UsingDirectiveSyntax>()
                .Select(u => u.Name?.ToString()).OfType<string>().ToList();

        static bool Related(string ns, string project) =>
            ns == project || ns.EndsWith("." + project, StringComparison.Ordinal) || project.EndsWith("." + ns, StringComparison.Ordinal);

        var filtered = matches.Where(m => hints.Any(h => Related(h, ProjectName(m)))).ToList();
        return filtered.Count > 0 ? filtered : matches;
    }

    /// <summary>
    /// Overloads of one client method are separate nodes when they target different routes. Keep the candidates whose
    /// parameter list accepts the call's argument count; falls back to all candidates when none (or all) do.
    /// </summary>
    private static List<ClientMethodNode> ByArgumentCount(List<ClientMethodNode> matches, int argCount)
    {
        var accepting = matches.Where(m => Accepts(m, argCount)).ToList();
        return accepting.Count > 0 ? accepting : matches;

        static bool Accepts(ClientMethodNode m, int args)
        {
            var open = m.Signature.IndexOf(" " + m.MethodName + "(", StringComparison.Ordinal);
            if (open < 0) return true;
            var ps = SyntaxFactory.ParseParameterList(m.Signature[(open + 1 + m.MethodName.Length)..]).Parameters;
            var unbounded = ps.Any(p => p.Modifiers.Any(SyntaxKind.ParamsKeyword));
            var required = ps.Count(p => p.Default is null && !p.Modifiers.Any(SyntaxKind.ParamsKeyword));
            return args >= required && (unbounded || args <= ps.Count);
        }
    }

    /// <summary>"proj:r/Api#2" → "Api".</summary>
    private static string ProjectName(ClientMethodNode m)
    {
        var name = m.ProjectId[(m.ProjectId.IndexOf('/') + 1)..];
        var hash = name.LastIndexOf('#');
        return hash >= 0 && name[(hash + 1)..].All(char.IsDigit) ? name[..hash] : name;
    }

    /// <summary>"Acme.Orders.IOrdersClient?" → "IOrdersClient"; "Wrapper&lt;T&gt;" → "Wrapper".</summary>
    private static string Normalize(string typeText)
    {
        var t = typeText.TrimEnd('?');
        var lt = t.IndexOf('<');
        if (lt >= 0) t = t[..lt];
        var dot = t.LastIndexOf('.');
        return dot >= 0 ? t[(dot + 1)..] : t;
    }

    private static (string Type, string Member) Containing(SyntaxNode n)
    {
        var type = n.Ancestors().OfType<BaseTypeDeclarationSyntax>().FirstOrDefault()?.Identifier.Text ?? "Program";
        var member = n.Ancestors().Select(a => a switch
        {
            LocalFunctionStatementSyntax lf => lf.Identifier.Text,
            MethodDeclarationSyntax m => m.Identifier.Text,
            ConstructorDeclarationSyntax => ".ctor",
            PropertyDeclarationSyntax p => p.Identifier.Text,
            _ => null,
        }).FirstOrDefault(s => s is not null) ?? "<top-level>";
        return (type, member);
    }
}
