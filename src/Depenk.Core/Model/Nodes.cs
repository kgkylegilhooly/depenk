using System.Text.Json.Serialization;

namespace Depenk.Core.Model;

public sealed record SourceLocation(string Path, int Line);

public sealed record RepoNode(string Id, string Name, string Path, string? HeadSha, bool Dirty);

public sealed record ProjectNode(string Id, string Repo, string Name, string Path, ProjectKind Kind,
    string? Sdk, string? PackageId, string? Version, bool IsPackable);

/// <summary>ProducerProjectIds empty = external; more than one = ambiguous.</summary>
public sealed record PackageNode(string Id, string PackageId, List<string> ProducerProjectIds)
{
    [JsonIgnore] public bool External => ProducerProjectIds.Count == 0;
}

public sealed record EndpointParameter(string Name, string Source, string TypeName, bool Required, string? Default);
public sealed record ResponseType(int StatusCode, string TypeName);

public sealed record EndpointNode(string Id, string Repo, string ProjectId, string Verb, string Route,
    string NormalizedRoute, string Handler, List<EndpointParameter> Parameters, List<ResponseType> Responses,
    SourceLocation Location);

public sealed record ClientMethodNode(string Id, string Repo, string ProjectId, string TypeName, string MethodName,
    string Signature, string? Verb, string? Route, string? NormalizedRoute, string Strategy, Confidence Confidence,
    SourceLocation Location);

public sealed record CallSiteNode(string Id, string Repo, string ProjectId, string ContainingMember,
    Confidence Confidence, SourceLocation Location);

public sealed record ModelField(string Name, string TypeName, bool Nullable, bool Collection);

public sealed record ModelNode(string Id, string Repo, string? ProjectId, string FullName, ModelKind Kind,
    List<ModelField> Fields, List<string>? EnumValues, SourceLocation? Location);
