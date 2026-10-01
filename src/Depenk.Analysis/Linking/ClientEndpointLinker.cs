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
                graph.Diagnostics.Add(new Diagnostic(DiagnosticKinds.UnresolvedClientMethod, Severities.Info, [cm.Id],
                    $"{cm.Id}: no HTTP call detected"));
                continue;
            }
            var route = cm.Route ?? cm.NormalizedRoute;
            var sameVerb = byRepo[cm.Repo].Where(e => e.Verb.Equals(cm.Verb, StringComparison.OrdinalIgnoreCase)).ToList();
            var exact = sameVerb.Where(e => e.NormalizedRoute == cm.NormalizedRoute).ToList();
            var (matches, strategy) = exact.Count > 0
                ? (exact, cm.Strategy)
                : (sameVerb.Where(e => IsSuffix(e.NormalizedRoute, cm.NormalizedRoute)).ToList(), cm.Strategy + "+suffix");

            if (matches.Count == 0)
            {
                graph.Diagnostics.Add(new Diagnostic(DiagnosticKinds.UnresolvedClientMethod, Severities.Info, [cm.Id],
                    $"{cm.Id}: no endpoint in repo '{cm.Repo}' matches {cm.Verb} {route}"));
            }
            else if (matches.Count == 1)
            {
                // a suffix match is a guess: Low; an exact match keeps the client method's own confidence
                var confidence = exact.Count == 1 ? cm.Confidence : Confidence.Low;
                graph.Edges.Add(new Edge(EdgeKind.Targets, cm.Id, matches[0].Id, confidence) { Strategy = strategy });
            }
            else
            {
                foreach (var ep in matches)
                    graph.Edges.Add(new Edge(EdgeKind.Targets, cm.Id, ep.Id, Confidence.Low) { Strategy = strategy });
                graph.Diagnostics.Add(new Diagnostic(DiagnosticKinds.AmbiguousRoute, Severities.Warning, [cm.Id, .. matches.Select(e => e.Id)],
                    $"{cm.Id} ({cm.Verb} {route}) matches {matches.Count} endpoints" +
                    (exact.Count == 0 ? " by route suffix" : "") + $": {string.Join(", ", matches.Select(e => e.Id))}"));
            }
        }
    }

    private static bool IsSuffix(string a, string b) =>
        a.Length > 0 && b.Length > 0 && (a.EndsWith("/" + b, StringComparison.Ordinal) || b.EndsWith("/" + a, StringComparison.Ordinal));
}
