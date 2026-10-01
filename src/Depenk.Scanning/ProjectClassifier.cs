using Depenk.Core.Model;
using Depenk.Scanning.Config;

namespace Depenk.Scanning;

public sealed record ProjectSignals(bool HasEndpoints, bool HasHttpClientCode);

public static class ProjectClassifier
{
    private static readonly string[] ClientSuffixes = [".Client", ".Contracts", ".Sdk"];

    public static ProjectKind Classify(ProjectFile pf, ProjectSignals signals, DepenkConfig config)
    {
        if (config.Projects.KindOverrides.TryGetValue(pf.Name, out var over)
            && Enum.TryParse<ProjectKind>(over, ignoreCase: true, out var forced))
            return forced;
        if (pf.IsTestProject) return ProjectKind.Test;
        if (string.Equals(pf.Sdk, "Microsoft.NET.Sdk.Web", StringComparison.OrdinalIgnoreCase) || signals.HasEndpoints)
            return ProjectKind.Api;
        var clientNamed = ClientSuffixes.Any(s => pf.Name.EndsWith(s, StringComparison.OrdinalIgnoreCase));
        if ((pf.IsPackable || clientNamed) && signals.HasHttpClientCode) return ProjectKind.Client;
        return pf.IsPackable ? ProjectKind.Library : ProjectKind.Other;
    }
}
