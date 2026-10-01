using Depenk.Core.Model;

namespace Depenk.Analysis.Linking;

public static class ClientEndpointLinker
{
    public static void Link(DepGraph graph)
    {
        var byRepo = graph.Endpoints.ToLookup(e => e.Repo);
        foreach (var cm in graph.ClientMethods)
        {
            if (cm.Verb is null || cm.NormalizedRoute is null)
            {
                graph.Diagnostics.Add(new Diagnostic(DiagnosticKinds.UnresolvedClientMethod, "info", [cm.Id],
                    $"{cm.Id}: no HTTP call detected"));
                continue;
            }
            var sameVerb = byRepo[cm.Repo].Where(e => e.Verb.Equals(cm.Verb, StringComparison.OrdinalIgnoreCase)).ToList();
            var exact = sameVerb.Where(e => e.NormalizedRoute == cm.NormalizedRoute).ToList();

            if (exact.Count == 1)
            {
                graph.Edges.Add(new Edge(EdgeKind.Targets, cm.Id, exact[0].Id, cm.Confidence) { Strategy = cm.Strategy });
            }
            else if (exact.Count > 1)
            {
                foreach (var ep in exact)
                    graph.Edges.Add(new Edge(EdgeKind.Targets, cm.Id, ep.Id, Confidence.Low) { Strategy = cm.Strategy });
                graph.Diagnostics.Add(new Diagnostic(DiagnosticKinds.AmbiguousRoute, "warning", [cm.Id, .. exact.Select(e => e.Id)],
                    $"{cm.Id} ({cm.Verb} {cm.Route}) matches {exact.Count} endpoints"));
            }
            else
            {
                var suffix = sameVerb.Where(e => IsSuffix(e.NormalizedRoute, cm.NormalizedRoute)).ToList();
                if (suffix.Count == 1)
                    graph.Edges.Add(new Edge(EdgeKind.Targets, cm.Id, suffix[0].Id, Confidence.Low) { Strategy = cm.Strategy + "+suffix" });
                else
                    graph.Diagnostics.Add(new Diagnostic(DiagnosticKinds.UnresolvedClientMethod, "info", [cm.Id],
                        $"{cm.Id}: no endpoint in repo '{cm.Repo}' matches {cm.Verb} {cm.Route}"));
            }
        }
    }

    private static bool IsSuffix(string a, string b) =>
        a.Length > 0 && b.Length > 0 && (a.EndsWith("/" + b, StringComparison.Ordinal) || b.EndsWith("/" + a, StringComparison.Ordinal));
}
