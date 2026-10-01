namespace Depenk.Core.Model;

public sealed class DepGraph
{
    public int SchemaVersion { get; init; } = 1;
    public string? Workspace { get; set; }
    public DateTimeOffset GeneratedAt { get; set; } = DateTimeOffset.UtcNow;
    public List<RepoNode> Repos { get; init; } = [];
    public List<ProjectNode> Projects { get; init; } = [];
    public List<PackageNode> Packages { get; init; } = [];
    public List<EndpointNode> Endpoints { get; init; } = [];
    public List<ClientMethodNode> ClientMethods { get; init; } = [];
    public List<CallSiteNode> CallSites { get; init; } = [];
    public List<ModelNode> Models { get; init; } = [];
    public List<Edge> Edges { get; init; } = [];
    public List<Diagnostic> Diagnostics { get; init; } = [];

    public IEnumerable<Edge> EdgesOf(EdgeKind kind) => Edges.Where(e => e.Kind == kind);
}
