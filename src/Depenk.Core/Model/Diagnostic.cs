namespace Depenk.Core.Model;

public sealed record Diagnostic(string Kind, string Severity, List<string> NodeIds, string Message);

public static class DiagnosticKinds
{
    public const string VersionDrift = "versionDrift", UnresolvedClientMethod = "unresolvedClientMethod",
        AmbiguousRoute = "ambiguousRoute", AmbiguousProducer = "ambiguousProducer",
        UnresolvedVersion = "unresolvedVersion", Cycle = "cycle", UnusedEndpoint = "unusedEndpoint",
        UnusedModel = "unusedModel", UnusedClientMethod = "unusedClientMethod", ParseError = "parseError",
        DuplicateProjectName = "duplicateProjectName";
}
