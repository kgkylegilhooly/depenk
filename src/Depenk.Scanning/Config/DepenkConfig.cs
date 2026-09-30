namespace Depenk.Scanning.Config;

public sealed class DepenkConfig
{
    public RepoFilter Repos { get; set; } = new();
    public ProjectOptions Projects { get; set; } = new();
    public PackageOptions Packages { get; set; } = new();
    public List<HttpWrapperConfig> HttpWrappers { get; set; } = [];
    public RouteOptions Routes { get; set; } = new();
}

public sealed class RepoFilter
{
    /// <summary>Explicit repo folders (relative to the workspace or absolute). When set, child-folder discovery is skipped.</summary>
    public List<string> Paths { get; set; } = [];
    public List<string> Include { get; set; } = ["*"];
    public List<string> Exclude { get; set; } = [];
}

public sealed class ProjectOptions
{
    /// <summary>Project name → ProjectKind name (case-insensitive), parsed by ProjectClassifier.</summary>
    public Dictionary<string, string> KindOverrides { get; set; } = [];
    public List<string> Ignore { get; set; } = [];
}

public sealed class PackageOptions
{
    /// <summary>PackageId → repo name that produces it.</summary>
    public Dictionary<string, string> Producers { get; set; } = [];
}

public sealed class HttpWrapperConfig
{
    public string Type { get; set; } = "";
    /// <summary>Method-name glob → HTTP verb.</summary>
    public Dictionary<string, string> Methods { get; set; } = [];
    public int RouteArgument { get; set; }
}

public sealed class RouteOptions
{
    /// <summary>Client project name → route prefix prepended to its routes.</summary>
    public Dictionary<string, string> Prefixes { get; set; } = [];
}
