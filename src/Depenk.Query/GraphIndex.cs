using System.Text.RegularExpressions;
using Depenk.Core.Model;

namespace Depenk.Query;

public sealed partial class GraphIndex
{
    private readonly Dictionary<string, NodeRef> _nodes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<Hop>> _deps = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<Hop>> _dependents = new(StringComparer.Ordinal);

    public GraphIndex(DepGraph graph)
    {
        Graph = graph;
        Repos = ById(graph.Repos, r => r.Id);
        Projects = ById(graph.Projects, p => p.Id);
        Packages = ById(graph.Packages, p => p.Id);
        Endpoints = ById(graph.Endpoints, e => e.Id);
        ClientMethods = ById(graph.ClientMethods, c => c.Id);
        CallSites = ById(graph.CallSites, c => c.Id);
        Models = ById(graph.Models, m => m.Id);

        foreach (var r in Repos.Values) Add(new NodeRef(r.Id, NodeKind.Repo, r.Name, r.Name));
        foreach (var p in Projects.Values) Add(new NodeRef(p.Id, NodeKind.Project, p.Name, p.Repo));
        foreach (var p in Packages.Values)
            Add(new NodeRef(p.Id, NodeKind.Package, p.PackageId,
                p.ProducerProjectIds.Select(id => Projects.GetValueOrDefault(id)?.Repo).FirstOrDefault(r => r is not null)));
        foreach (var e in Endpoints.Values) Add(new NodeRef(e.Id, NodeKind.Endpoint, $"{e.Verb} {e.Route}", e.Repo));
        foreach (var c in ClientMethods.Values) Add(new NodeRef(c.Id, NodeKind.ClientMethod, $"{c.TypeName}.{c.MethodName}", c.Repo));
        foreach (var c in CallSites.Values) Add(new NodeRef(c.Id, NodeKind.CallSite, c.ContainingMember, c.Repo));
        foreach (var m in Models.Values)
            Add(new NodeRef(m.Id, NodeKind.Model, m.FullName, string.IsNullOrEmpty(m.Repo) ? null : m.Repo));

        foreach (var e in graph.Edges)
        {
            var hop = e.Kind == EdgeKind.Produces
                ? new Hop(e.To, e.From, e.Kind, e.Confidence, e)
                : new Hop(e.From, e.To, e.Kind, e.Confidence, e);
            Append(_deps, hop.From, hop);
            Append(_dependents, hop.To, hop);
        }
    }

    public DepGraph Graph { get; }
    public IReadOnlyDictionary<string, RepoNode> Repos { get; }
    public IReadOnlyDictionary<string, ProjectNode> Projects { get; }
    public IReadOnlyDictionary<string, PackageNode> Packages { get; }
    public IReadOnlyDictionary<string, EndpointNode> Endpoints { get; }
    public IReadOnlyDictionary<string, ClientMethodNode> ClientMethods { get; }
    public IReadOnlyDictionary<string, CallSiteNode> CallSites { get; }
    public IReadOnlyDictionary<string, ModelNode> Models { get; }
    public IReadOnlyCollection<NodeRef> Nodes => _nodes.Values;

    public bool TryGet(string id, out NodeRef node) => _nodes.TryGetValue(id, out node!);

    public NodeRef Get(string id) => TryGet(id, out var n) ? n : throw NotFound(id);

    public IReadOnlyList<Hop> DependenciesOf(string id) => _deps.TryGetValue(id, out var l) ? l : [];
    public IReadOnlyList<Hop> DependentsOf(string id) => _dependents.TryGetValue(id, out var l) ? l : [];

    public string ResolveRepo(string q) =>
        _nodes.ContainsKey(q) && _nodes[q].Kind == NodeKind.Repo ? q
        : Repos.Values.FirstOrDefault(r => r.Name.Equals(q, StringComparison.OrdinalIgnoreCase))?.Id ?? throw NotFound(q);

    public string ResolvePackage(string q) =>
        Packages.ContainsKey(q) ? q
        : Packages.Values.FirstOrDefault(p => p.PackageId.Equals(q, StringComparison.OrdinalIgnoreCase))?.Id ?? throw NotFound(q);

    public string ResolveEndpoint(string q)
    {
        if (Endpoints.ContainsKey(q)) return q;
        var space = q.Trim().IndexOf(' ');
        if (space <= 0) throw NotFound(q);
        var verb = q.Trim()[..space].ToUpperInvariant();
        var route = q.Trim()[(space + 1)..].Trim();
        var sameVerb = Endpoints.Values.Where(e => e.Verb.Equals(verb, StringComparison.OrdinalIgnoreCase)).ToList();
        var exact = sameVerb.Where(e => e.Route.Equals(route, StringComparison.OrdinalIgnoreCase)).ToList();
        var matches = exact.Count > 0 ? exact
            : sameVerb.Where(e => e.NormalizedRoute == NormalizeRoute(route)).ToList();
        return matches.Count switch
        {
            1 => matches[0].Id,
            0 => throw NotFound(q),
            _ => throw new QueryException(QueryException.Ambiguous, $"'{q}' matches {matches.Count} endpoints",
                "Pass one of the suggested endpoint ids.", matches.Select(m => m.Id).Order(StringComparer.Ordinal).ToList()),
        };
    }

    public string ResolveModel(string q)
    {
        if (Models.ContainsKey(q)) return q;
        var full = Models.Values.Where(m => m.FullName == q).ToList();
        var matches = full.Count > 0 ? full : Models.Values.Where(m => SimpleName(m.FullName) == q).ToList();
        return matches.Count switch
        {
            1 => matches[0].Id,
            0 => throw NotFound(q),
            _ => throw new QueryException(QueryException.Ambiguous, $"'{q}' matches {matches.Count} models",
                "Pass one of the suggested model ids.", matches.Select(m => m.Id).Order(StringComparer.Ordinal).ToList()),
        };
    }

    public string ResolveAny(string q)
    {
        if (_nodes.ContainsKey(q)) return q;
        var repo = Repos.Values.FirstOrDefault(r => r.Name.Equals(q, StringComparison.OrdinalIgnoreCase));
        if (repo is not null) return repo.Id;
        var pkg = Packages.Values.FirstOrDefault(p => p.PackageId.Equals(q, StringComparison.OrdinalIgnoreCase));
        if (pkg is not null) return pkg.Id;
        if (q.Trim().Contains(' ')) return ResolveEndpoint(q);
        return ResolveModel(q);
    }

    public IReadOnlyList<string> Suggest(string q, int max = 5)
    {
        var needle = q.ToLowerInvariant();
        return _nodes.Values
            .Select(n => (n.Id,
                Contains: n.Id.Contains(needle, StringComparison.OrdinalIgnoreCase) || n.Label.Contains(needle, StringComparison.OrdinalIgnoreCase),
                Dist: Math.Min(Fuzzy.Distance(n.Id.ToLowerInvariant(), needle), Fuzzy.Distance(n.Label.ToLowerInvariant(), needle))))
            .OrderBy(x => x.Contains ? 0 : 1).ThenBy(x => x.Dist).ThenBy(x => x.Id, StringComparer.Ordinal)
            .Take(max).Select(x => x.Id).ToList();
    }

    public QueryException NotFound(string q) =>
        new(QueryException.NotFound, $"No node matches '{q}'",
            "Use list_repos or find_endpoints to discover ids; endpoints also accept 'VERB /route'.", Suggest(q));

    private void Add(NodeRef n) => _nodes.TryAdd(n.Id, n);

    private static void Append(Dictionary<string, List<Hop>> map, string key, Hop hop)
    {
        if (!map.TryGetValue(key, out var list)) map[key] = list = [];
        list.Add(hop);
    }

    private static IReadOnlyDictionary<string, T> ById<T>(IEnumerable<T> items, Func<T, string> id) =>
        items.GroupBy(id).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

    private static string SimpleName(string fullName)
    {
        var s = fullName[(fullName.LastIndexOf('.') + 1)..];
        var tick = s.IndexOf('`');
        return tick >= 0 ? s[..tick] : s;
    }

    private static string NormalizeRoute(string route) =>
        string.Join('/', Placeholder().Replace(route.Split('?', '#')[0], "{}")
            .Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)).ToLowerInvariant();

    [GeneratedRegex(@"\{[^{}]*\}")]
    private static partial Regex Placeholder();
}
