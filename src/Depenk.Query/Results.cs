using Depenk.Core.Model;

namespace Depenk.Query;

public interface ITruncatable { bool Truncated { get; } }

public sealed record Paged<T>(List<T> Items, int Total, bool Truncated, string? Hint) : ITruncatable;

// list_repos / get_repo
public sealed record RepoLink(string From, string To, Confidence Confidence, List<string> ViaPackages, int CallCount);
public sealed record RepoSummary(string Id, string Name, int Projects, int Endpoints, int ClientMethods, int Diagnostics,
    List<string> DependsOn, List<string> DependedOnBy);
public sealed record ListReposResult(List<RepoSummary> Repos, List<RepoLink> Links);
public sealed record ProjectSummary(string Id, string Name, ProjectKind Kind, string Path);
public sealed record PackageVersion(string PackageId, string? Version, string ProjectId);
public sealed record ConsumedPackage(string PackageId, string? Version, string ConsumerProjectId, string? ProducerRepo);
public sealed record DiagnosticCount(string Kind, int Count);
public sealed record RepoDetail(string Id, string Name, string Path, string? HeadSha, List<ProjectSummary> Projects,
    List<PackageVersion> Publishes, List<ConsumedPackage> Consumes, List<RepoLink> DependsOn, List<RepoLink> DependedOnBy,
    int Endpoints, List<DiagnosticCount> Diagnostics);

// endpoints
public sealed record EndpointSummary(string Id, string Verb, string Route, string Handler, string Repo, int Callers);
public sealed record ModelRef(string Id, string FullName, string Repo, ModelKind Kind, Confidence Confidence, string? Source);
public sealed record ResponseRef(int StatusCode, string TypeName, List<ModelRef> Models);
public sealed record ClientMethodRef(string Id, string Type, string Method, string Signature, string Repo, string? PackageId,
    string Strategy, Confidence Confidence);
public sealed record CallSiteRef(string Id, string Repo, string ProjectId, string Member, SourceLocation Location,
    Confidence Confidence, string ClientMethodId);
public sealed record EndpointDetail(string Id, string Verb, string Route, string Repo, string ProjectId, string Handler,
    SourceLocation Location, List<EndpointParameter> Parameters, List<ModelRef> Accepts, List<ResponseRef> Returns,
    List<ClientMethodRef> ClientMethods, List<CallSiteRef> Callers);

// diagnostics
public sealed record DiagnosticsResult(List<Diagnostic> Items, int Total, bool Truncated, List<DiagnosticCount> ByKind)
    : ITruncatable;

// models (Task 3)
public sealed record FieldNode(string Name, string TypeName, bool Nullable, bool Collection, bool CrossRepo,
    List<ModelTree>? Types);
public sealed record ModelTree(string Id, string FullName, ModelKind Kind, string Repo, string? ProjectId,
    SourceLocation? Location, List<string>? EnumValues, List<FieldNode> Fields);
public sealed record UsageRef(string EndpointId, string Relation, string Via, Confidence Confidence, string Repo);
public sealed record ModelUsages(string ModelId, List<UsageRef> Endpoints, List<string> ContainedIn, List<string> Repos);

// trace / impact (Task 3)
public sealed record TraceStep(string Id, NodeKind Kind, string Label, string Direction, int Depth, string ViaFrom,
    EdgeKind ViaKind, Confidence Confidence);
public sealed record TraceResult(string Root, string Direction, List<TraceStep> Nodes, bool Truncated) : ITruncatable;
public sealed record Affected(string Id, string Label, string Repo, Confidence Confidence);
public sealed record ImpactResult(string Target, string TargetKind, string? Field, List<Affected> Repos,
    List<Affected> Projects, List<Affected> Endpoints, List<Affected> ClientMethods, List<Affected> CallSites,
    List<Affected> Models);

// how_to_call (Task 3)
public sealed record CallOption(string PackageId, string? LatestVersion, string ProducerProjectId, string Type,
    string Method, string Signature, Confidence Confidence, List<ModelRef> Request, List<ResponseRef> Response);
public sealed record HowToCallResult(string EndpointId, List<CallOption> Options, string? Hint);

// get_source (Task 4)
public sealed record SourceSnippet(string NodeId, string Path, int Line, int StartLine, List<string> Lines);
