using Depenk.Core.Model;
using Depenk.Scanning;

namespace Depenk.Analysis.Diagnostics;

public static class GraphDiagnostics
{
    public static void Add(DepGraph g)
    {
        VersionDrift(g);
        Cycles(g);
        Unused(g);
    }

    private static void VersionDrift(DepGraph g)
    {
        foreach (var pkg in g.Packages.Where(p => !p.External))
        {
            var refs = g.EdgesOf(EdgeKind.References)
                .Where(e => e.To == pkg.Id && e.Version is not null && !ProjectFile.IsUnresolved(e.Version)).ToList();
            if (refs.Count == 0) continue;
            var produced = g.EdgesOf(EdgeKind.Produces)
                .Where(e => e.To == pkg.Id && e.Version is not null && !ProjectFile.IsUnresolved(e.Version))
                .Select(e => e.Version!).FirstOrDefault();
            var latest = produced ?? refs.Select(r => r.Version!).OrderBy(v => v, VersionComparer.Instance).Last();
            var behind = refs.Where(r => r.Version != latest).OrderBy(r => r.From, StringComparer.Ordinal).ToList();
            if (behind.Count == 0) continue;
            g.Diagnostics.Add(new Diagnostic(DiagnosticKinds.VersionDrift, "warning",
                [pkg.Id, .. behind.Select(b => b.From)],
                $"{pkg.PackageId}: latest {latest}; behind: {string.Join(", ", behind.Select(b => $"{b.From} ({b.Version})"))}"));
        }
    }

    private static void Cycles(DepGraph g)
    {
        var adj = g.EdgesOf(EdgeKind.DependsOn).GroupBy(e => e.From).ToDictionary(x => x.Key, x => x.Select(e => e.To).ToList());
        var nodes = adj.Keys.Concat(adj.Values.SelectMany(v => v)).Distinct().Order(StringComparer.Ordinal).ToList();

        // Tarjan's strongly connected components
        var index = 0;
        var indices = new Dictionary<string, int>();
        var low = new Dictionary<string, int>();
        var stack = new Stack<string>();
        var onStack = new HashSet<string>();
        var sccs = new List<List<string>>();

        void Visit(string v)
        {
            indices[v] = low[v] = index++;
            stack.Push(v); onStack.Add(v);
            foreach (var w in adj.GetValueOrDefault(v, []))
            {
                if (!indices.ContainsKey(w)) { Visit(w); low[v] = Math.Min(low[v], low[w]); }
                else if (onStack.Contains(w)) low[v] = Math.Min(low[v], indices[w]);
            }
            if (low[v] != indices[v]) return;
            var scc = new List<string>();
            string x;
            do { x = stack.Pop(); onStack.Remove(x); scc.Add(x); } while (x != v);
            sccs.Add(scc);
        }

        foreach (var n in nodes) if (!indices.ContainsKey(n)) Visit(n);

        foreach (var scc in sccs.Where(s => s.Count > 1))
        {
            var ids = scc.Order(StringComparer.Ordinal).ToList();
            var names = ids.Select(i => i["repo:".Length..]).ToList();
            g.Diagnostics.Add(new Diagnostic(DiagnosticKinds.Cycle, "warning", ids,
                $"Circular dependency: {string.Join(" → ", names)} → {names[0]}"));
        }
    }

    private static void Unused(DepGraph g)
    {
        var targeted = g.EdgesOf(EdgeKind.Targets).Select(e => e.To).ToHashSet();
        var reposWithClients = g.ClientMethods.Select(c => c.Repo).ToHashSet();
        foreach (var ep in g.Endpoints.Where(e => reposWithClients.Contains(e.Repo) && !targeted.Contains(e.Id)))
            g.Diagnostics.Add(new Diagnostic(DiagnosticKinds.UnusedEndpoint, "info", [ep.Id],
                $"{ep.Verb} {ep.Route} ({ep.Handler}) is not called by any client method"));

        var invoked = g.EdgesOf(EdgeKind.Invokes).Select(e => e.To).ToHashSet();
        // an interface method and its implementation share (project, method name): either being invoked counts for both
        var invokedNames = g.ClientMethods.Where(c => invoked.Contains(c.Id)).Select(c => (c.ProjectId, c.MethodName)).ToHashSet();
        foreach (var cm in g.ClientMethods.Where(c => c.Verb is not null && !invokedNames.Contains((c.ProjectId, c.MethodName))))
            g.Diagnostics.Add(new Diagnostic(DiagnosticKinds.UnusedClientMethod, "info", [cm.Id],
                $"{cm.TypeName}.{cm.MethodName} has no call sites in the workspace"));

        var clientProjects = g.Projects.Where(p => p.Kind == ProjectKind.Client).Select(p => p.Id).ToHashSet();
        var referencedModels = g.Edges.Where(e => e.Kind is EdgeKind.Accepts or EdgeKind.Returns or EdgeKind.FieldOf)
            .Select(e => e.To).ToHashSet();
        foreach (var m in g.Models.Where(m => m.ProjectId is not null && clientProjects.Contains(m.ProjectId)
                                              && !referencedModels.Contains(m.Id)))
            g.Diagnostics.Add(new Diagnostic(DiagnosticKinds.UnusedModel, "info", [m.Id],
                $"{m.FullName} is not used by any endpoint or model"));
    }

    private sealed class VersionComparer : IComparer<string>
    {
        public static readonly VersionComparer Instance = new();

        public int Compare(string? a, string? b)
        {
            if (Version.TryParse(a?.Split('-')[0], out var va) && Version.TryParse(b?.Split('-')[0], out var vb))
                return va.CompareTo(vb);
            return string.CompareOrdinal(a, b);
        }
    }
}
