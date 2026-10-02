using Depenk.Core;
using Depenk.Core.Model;

namespace Depenk.Query;

public sealed partial class QueryService(GraphIndex index)
{
    public const int DefaultLimit = 50, MaxLimit = 500;

    public GraphIndex Index { get; } = index;
    public DepGraph Graph => Index.Graph;

    public ListReposResult ListRepos()
    {
        var links = Links();
        var repos = Index.Repos.Values.OrderBy(r => r.Name, StringComparer.Ordinal).Select(r => new RepoSummary(
            r.Id, r.Name,
            Graph.Projects.Count(p => p.Repo == r.Name),
            Graph.Endpoints.Count(e => e.Repo == r.Name),
            Graph.ClientMethods.Count(c => c.Repo == r.Name),
            DiagnosticsFor(r.Name).Count(),
            links.Where(l => l.From == r.Id).Select(l => l.To).ToList(),
            links.Where(l => l.To == r.Id).Select(l => l.From).ToList())).ToList();
        return new ListReposResult(repos, links);
    }

    public RepoDetail GetRepo(string repo)
    {
        var r = Index.Repos[Index.ResolveRepo(repo)];
        var projects = Graph.Projects.Where(p => p.Repo == r.Name).OrderBy(p => p.Id, StringComparer.Ordinal).ToList();
        var projectIds = projects.Select(p => p.Id).ToHashSet();
        var publishes = Graph.EdgesOf(EdgeKind.Produces).Where(e => projectIds.Contains(e.From))
            .Select(e => new PackageVersion(Index.Packages[e.To].PackageId, e.Version, e.From))
            .OrderBy(p => p.PackageId, StringComparer.Ordinal).ToList();
        var consumes = Graph.EdgesOf(EdgeKind.References)
            .Where(e => projectIds.Contains(e.From) && Index.Packages.TryGetValue(e.To, out var p) && !p.External)
            .Select(e => new ConsumedPackage(Index.Packages[e.To].PackageId, e.Version, e.From, Index.Get(e.To).Repo))
            .Where(c => c.ProducerRepo != r.Name)
            .OrderBy(c => c.PackageId, StringComparer.Ordinal).ThenBy(c => c.ConsumerProjectId, StringComparer.Ordinal).ToList();
        var links = Links();
        return new RepoDetail(r.Id, r.Name, r.Path, r.HeadSha,
            projects.Select(p => new ProjectSummary(p.Id, p.Name, p.Kind, p.Path)).ToList(),
            publishes, consumes,
            links.Where(l => l.From == r.Id).ToList(),
            links.Where(l => l.To == r.Id).ToList(),
            Graph.Endpoints.Count(e => e.Repo == r.Name),
            CountByKind(DiagnosticsFor(r.Name)));
    }

    public Paged<EndpointSummary> FindEndpoints(string? query, string? repo = null, string? verb = null, int? limit = null)
    {
        var repoName = repo is null ? null : Index.Repos[Index.ResolveRepo(repo)].Name;
        var matches = Graph.Endpoints
            .Where(e => string.IsNullOrEmpty(query)
                        || e.Route.Contains(query, StringComparison.OrdinalIgnoreCase)
                        || e.Handler.Contains(query, StringComparison.OrdinalIgnoreCase))
            .Where(e => repoName is null || e.Repo == repoName)
            .Where(e => verb is null || e.Verb.Equals(verb, StringComparison.OrdinalIgnoreCase))
            .OrderBy(e => e.Id, StringComparer.Ordinal)
            .Select(e => new EndpointSummary(e.Id, e.Verb, e.Route, e.Handler, e.Repo,
                Index.DependentsOf(e.Id).Count(h => h.Kind == EdgeKind.Targets)))
            .ToList();
        return Page(matches, limit, "Narrow with query, repo or verb.");
    }

    public EndpointDetail GetEndpoint(string endpoint)
    {
        var ep = Index.Endpoints[Index.ResolveEndpoint(endpoint)];
        var deps = Index.DependenciesOf(ep.Id);
        var accepts = deps.Where(h => h.Kind == EdgeKind.Accepts)
            .Select(h => ToModelRef(h.To, h.Confidence, h.Edge.Source)).ToList();
        var returns = Responses(ep, deps);
        var clientMethods = Index.DependentsOf(ep.Id).Where(h => h.Kind == EdgeKind.Targets)
            .Select(h => ToClientMethodRef(Index.ClientMethods[h.From], h))
            .OrderBy(c => c.Id, StringComparer.Ordinal).ToList();
        var callers = clientMethods
            .SelectMany(cm => Index.DependentsOf(cm.Id).Where(h => h.Kind == EdgeKind.Invokes)
                .Select(h => (Site: Index.CallSites[h.From], Hop: h, ClientMethod: cm.Id)))
            .Select(x => new CallSiteRef(x.Site.Id, x.Site.Repo, x.Site.ProjectId, x.Site.ContainingMember,
                x.Site.Location, x.Hop.Confidence, x.ClientMethod))
            .OrderBy(c => c.Id, StringComparer.Ordinal).ToList();
        return new EndpointDetail(ep.Id, ep.Verb, ep.Route, ep.Repo, ep.ProjectId, ep.Handler, ep.Location,
            ep.Parameters, accepts, returns, clientMethods, callers);
    }

    public DiagnosticsResult GetDiagnostics(string? kind = null, string? repo = null, string? severity = null, int? limit = null)
    {
        IEnumerable<Diagnostic> items = Graph.Diagnostics;
        if (repo is not null) items = DiagnosticsFor(Index.Repos[Index.ResolveRepo(repo)].Name);
        if (kind is not null) items = items.Where(d => d.Kind.Equals(kind, StringComparison.OrdinalIgnoreCase));
        if (severity is not null) items = items.Where(d => d.Severity.Equals(severity, StringComparison.OrdinalIgnoreCase));
        var all = items.ToList();
        var cap = Cap(limit);
        return new DiagnosticsResult(all.Take(cap).ToList(), all.Count, all.Count > cap, CountByKind(all));
    }

    internal ModelRef ToModelRef(string id, Confidence c, string? source)
    {
        var m = Index.Models[id];
        return new ModelRef(m.Id, m.FullName, m.Repo, m.Kind, c, source);
    }

    internal List<ResponseRef> Responses(EndpointNode ep, IReadOnlyList<Hop> deps) =>
        ep.Responses.GroupBy(r => r.StatusCode).OrderBy(g => g.Key).Select(g => new ResponseRef(g.Key, g.First().TypeName,
            deps.Where(h => h.Kind == EdgeKind.Returns && h.Edge.StatusCode == g.Key)
                .Select(h => ToModelRef(h.To, h.Confidence, null)).ToList())).ToList();

    internal ClientMethodRef ToClientMethodRef(ClientMethodNode cm, Hop target) =>
        new(cm.Id, cm.TypeName, cm.MethodName, cm.Signature, cm.Repo, PackageOf(cm.ProjectId),
            target.Edge.Strategy ?? cm.Strategy, target.Confidence);

    internal string? PackageOf(string projectId) =>
        Graph.EdgesOf(EdgeKind.Produces).Where(e => e.From == projectId)
            .Select(e => Index.Packages.GetValueOrDefault(e.To)?.PackageId).FirstOrDefault(p => p is not null);

    internal IEnumerable<Diagnostic> DiagnosticsFor(string repoName) =>
        Graph.Diagnostics.Where(d => d.NodeIds.Any(id =>
            id == Ids.Repo(repoName) || (Index.TryGet(id, out var n) && n.Repo == repoName)));

    internal static int Cap(int? limit) => Math.Clamp(limit ?? DefaultLimit, 1, MaxLimit);

    private Paged<T> Page<T>(List<T> all, int? limit, string hint)
    {
        var cap = Cap(limit);
        var truncated = all.Count > cap;
        return new Paged<T>(all.Take(cap).ToList(), all.Count, truncated, truncated ? hint : null);
    }

    private List<RepoLink> Links() =>
        Graph.EdgesOf(EdgeKind.DependsOn)
            .Select(e => new RepoLink(e.From, e.To, e.Confidence, e.ViaPackages ?? [], e.CallCount ?? 0))
            .OrderBy(l => l.From, StringComparer.Ordinal).ThenBy(l => l.To, StringComparer.Ordinal).ToList();

    private static List<DiagnosticCount> CountByKind(IEnumerable<Diagnostic> ds) =>
        ds.GroupBy(d => d.Kind).OrderBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => new DiagnosticCount(g.Key, g.Count())).ToList();
}
