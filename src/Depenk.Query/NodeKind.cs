using Depenk.Core.Model;

namespace Depenk.Query;

public enum NodeKind { Repo, Project, Package, Endpoint, ClientMethod, CallSite, Model }

public sealed record NodeRef(string Id, NodeKind Kind, string Label, string? Repo);

/// <summary>A dependency hop: <see cref="From"/> depends on <see cref="To"/>. <see cref="Edge"/> is the original edge.</summary>
public sealed record Hop(string From, string To, EdgeKind Kind, Confidence Confidence, Edge Edge);
