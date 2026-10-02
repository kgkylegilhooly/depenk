using System.Text.RegularExpressions;
using Depenk.Core.Model;

namespace Depenk.Query;

public sealed partial class QueryService
{
    public ModelTree GetModel(string model, int depth = 2) =>
        Tree(Index.ResolveModel(model), Math.Clamp(depth, 0, 6), new HashSet<string>(StringComparer.Ordinal));

    public ModelUsages FindModelUsages(string model)
    {
        var id = Index.ResolveModel(model);
        var targets = Relax(id, h => h.Kind == EdgeKind.FieldOf);
        var usages = targets
            .SelectMany(t => Index.DependentsOf(t.Key)
                .Where(h => h.Kind is EdgeKind.Accepts or EdgeKind.Returns)
                .Select(h => new UsageRef(h.From, h.Kind == EdgeKind.Accepts ? "accepts" : "returns", t.Key,
                    Min(t.Value, h.Confidence), Index.Endpoints[h.From].Repo)))
            .DistinctBy(u => (u.EndpointId, u.Relation, u.Via))
            .OrderBy(u => u.EndpointId, StringComparer.Ordinal).ThenBy(u => u.Via, StringComparer.Ordinal).ToList();
        var repos = usages.Select(u => u.Repo)
            .Concat(usages.SelectMany(u => CallerRepos(u.EndpointId)))
            .Distinct().Select(r => $"repo:{r}").Order(StringComparer.Ordinal).ToList();
        return new ModelUsages(id, usages,
            targets.Keys.Where(k => k != id).Order(StringComparer.Ordinal).ToList(), repos);
    }

    public TraceResult Trace(string node, string direction = "down", int depth = 3, int? limit = null)
    {
        var root = Index.ResolveAny(node);
        var dir = direction.ToLowerInvariant();
        string[] dirs = dir switch
        {
            "down" => ["down"],
            "up" => ["up"],
            "both" => ["down", "up"],
            _ => throw new QueryException(QueryException.InvalidArgument,
                $"direction must be up, down or both (got '{direction}')", "Use direction: \"down\" for dependencies, \"up\" for dependents."),
        };
        var maxDepth = Math.Clamp(depth, 1, 10);
        var steps = new List<TraceStep>();
        foreach (var d in dirs)
        {
            var visited = new HashSet<string>(StringComparer.Ordinal) { root };
            var queue = new Queue<(string Id, int Depth, Confidence Conf)>([(root, 0, Confidence.Certain)]);
            while (queue.TryDequeue(out var cur))
            {
                if (cur.Depth >= maxDepth) continue;
                var hops = d == "down" ? Index.DependenciesOf(cur.Id) : Index.DependentsOf(cur.Id);
                foreach (var h in hops.OrderBy(h => d == "down" ? h.To : h.From, StringComparer.Ordinal))
                {
                    var next = d == "down" ? h.To : h.From;
                    if (!visited.Add(next) || !Index.TryGet(next, out var n)) continue;
                    var conf = Min(cur.Conf, h.Confidence);
                    steps.Add(new TraceStep(next, n.Kind, n.Label, d, cur.Depth + 1, cur.Id, h.Kind, conf));
                    queue.Enqueue((next, cur.Depth + 1, conf));
                }
            }
        }
        var cap = Cap(limit);
        return new TraceResult(root, dir, steps.Take(cap).ToList(), steps.Count > cap);
    }

    public ImpactResult ImpactOfChange(string target)
    {
        string start;
        string? field = null;
        try
        {
            start = Index.ResolveAny(target);
        }
        catch (QueryException ex) when (ex.Code == QueryException.NotFound && target.Contains('.'))
        {
            var dot = target.LastIndexOf('.');
            start = Index.ResolveModel(target[..dot]);
            field = target[(dot + 1)..];
            if (Index.Models[start].Fields.All(f => f.Name != field)) throw Index.NotFound(target);
        }

        var best = Relax(start, _ => true);
        best.Remove(start);

        var rolled = new Dictionary<string, Confidence>(best, StringComparer.Ordinal);
        void Bump(string id, Confidence c)
        {
            if (!rolled.TryGetValue(id, out var old) || c > old) rolled[id] = c;
        }
        foreach (var (id, c) in best)
        {
            var n = Index.Get(id);
            var projectId = ProjectIdOf(id);
            if (projectId is not null && projectId != start) Bump(projectId, c);
            if (!string.IsNullOrEmpty(n.Repo) && $"repo:{n.Repo}" != start) Bump($"repo:{n.Repo}", c);
        }

        List<Affected> Of(NodeKind kind) => rolled
            .Where(kv => Index.TryGet(kv.Key, out var n) && n.Kind == kind)
            .Select(kv => { var n = Index.Get(kv.Key); return new Affected(n.Id, n.Label, n.Repo ?? "", kv.Value); })
            .OrderBy(a => a.Id, StringComparer.Ordinal).ToList();

        var kindName = field is not null ? "field" : JsonName(Index.Get(start).Kind);
        return new ImpactResult(start, kindName, field, Of(NodeKind.Repo), Of(NodeKind.Project), Of(NodeKind.Endpoint),
            Of(NodeKind.ClientMethod), Of(NodeKind.CallSite), Of(NodeKind.Model));
    }

    public HowToCallResult HowToCall(string endpoint)
    {
        var ep = Index.Endpoints[Index.ResolveEndpoint(endpoint)];
        var deps = Index.DependenciesOf(ep.Id);
        var request = deps.Where(h => h.Kind == EdgeKind.Accepts).Select(h => ToModelRef(h.To, h.Confidence, h.Edge.Source)).ToList();
        var response = Responses(ep, deps);
        var options = Index.DependentsOf(ep.Id).Where(h => h.Kind == EdgeKind.Targets)
            .Select(h => (Cm: Index.ClientMethods[h.From], Hop: h))
            .Select(x => (x.Cm, x.Hop, Produces: Graph.EdgesOf(EdgeKind.Produces).FirstOrDefault(e => e.From == x.Cm.ProjectId)))
            .Where(x => x.Produces is not null && Index.Packages.ContainsKey(x.Produces.To))
            .Select(x => new CallOption(Index.Packages[x.Produces!.To].PackageId, x.Produces.Version, x.Cm.ProjectId,
                x.Cm.TypeName, x.Cm.MethodName, x.Cm.Signature, x.Hop.Confidence, request, response))
            .OrderBy(o => InterfaceName().IsMatch(o.Type) ? 0 : 1)
            .ThenBy(o => $"{o.ProducerProjectId}:{o.Type}.{o.Method}", StringComparer.Ordinal)
            .ToList();
        return new HowToCallResult(ep.Id, options, options.Count > 0 ? null
            : "No client package method targets this endpoint; call it over HTTP directly or add a method to its client package.");
    }

    public string Overview() => OverviewWriter.Write(this);

    internal static Confidence Min(Confidence a, Confidence b) => a < b ? a : b;

    /// <summary>Best (max over paths of min along path) confidence for every node reachable via dependents.</summary>
    private Dictionary<string, Confidence> Relax(string start, Func<Hop, bool> follow)
    {
        var best = new Dictionary<string, Confidence>(StringComparer.Ordinal) { [start] = Confidence.Certain };
        var queue = new Queue<string>([start]);
        while (queue.TryDequeue(out var cur))
        {
            foreach (var h in Index.DependentsOf(cur).Where(follow))
            {
                var c = Min(best[cur], h.Confidence);
                if (best.TryGetValue(h.From, out var old) && c <= old) continue;
                best[h.From] = c;
                queue.Enqueue(h.From);
            }
        }
        return best;
    }

    private ModelTree Tree(string id, int depth, HashSet<string> path)
    {
        var m = Index.Models[id];
        path.Add(id);
        var childHops = Index.DependenciesOf(id).Where(h => h.Kind == EdgeKind.FieldOf).ToList();
        var fields = m.Fields.Select(f =>
        {
            var hops = childHops.Where(h => h.Edge.FieldName == f.Name).ToList();
            var cross = hops.Any(h => Index.Models[h.To].Repo is { Length: > 0 } r && r != m.Repo);
            List<ModelTree>? types = depth > 0 && hops.Count > 0
                ? hops.Select(h => path.Contains(h.To) ? Stub(h.To) : Tree(h.To, depth - 1, path)).ToList()
                : null;
            return new FieldNode(f.Name, f.TypeName, f.Nullable, f.Collection, cross, types);
        }).ToList();
        path.Remove(id);
        return new ModelTree(m.Id, m.FullName, m.Kind, m.Repo, m.ProjectId, m.Location, m.EnumValues, fields);
    }

    private ModelTree Stub(string id)
    {
        var m = Index.Models[id];
        return new ModelTree(m.Id, m.FullName, m.Kind, m.Repo, m.ProjectId, m.Location, m.EnumValues, []);
    }

    private IEnumerable<string> CallerRepos(string endpointId) =>
        Index.DependentsOf(endpointId).Where(h => h.Kind == EdgeKind.Targets)
            .SelectMany(h => Index.DependentsOf(h.From).Where(x => x.Kind == EdgeKind.Invokes))
            .Select(h => Index.CallSites[h.From].Repo);

    private string? ProjectIdOf(string id) =>
        Index.Endpoints.GetValueOrDefault(id)?.ProjectId
        ?? Index.ClientMethods.GetValueOrDefault(id)?.ProjectId
        ?? Index.CallSites.GetValueOrDefault(id)?.ProjectId
        ?? Index.Models.GetValueOrDefault(id)?.ProjectId;

    private static string JsonName(NodeKind k) => char.ToLowerInvariant(k.ToString()[0]) + k.ToString()[1..];

    [GeneratedRegex("^I[A-Z]")]
    private static partial Regex InterfaceName();
}
