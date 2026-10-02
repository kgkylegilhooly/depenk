using System.Text;
using Depenk.Core.Model;

namespace Depenk.Query;

public static class OverviewWriter
{
    public static string Write(QueryService q)
    {
        var g = q.Graph;
        var repos = q.ListRepos();
        var sb = new StringBuilder();
        sb.AppendLine("# depenk workspace overview").AppendLine();
        sb.AppendLine($"Scanned {g.GeneratedAt:u}. {g.Repos.Count} repos · {g.Projects.Count} projects · " +
                      $"{g.Endpoints.Count} endpoints · {g.ClientMethods.Count} client methods · " +
                      $"{g.CallSites.Count} call sites · {g.Models.Count} models").AppendLine();

        sb.AppendLine("## Repos").AppendLine();
        sb.AppendLine("| Repo | Projects | Endpoints | Client methods | Depends on | Depended on by |");
        sb.AppendLine("|---|---|---|---|---|---|");
        foreach (var r in repos.Repos)
            sb.AppendLine($"| {r.Name} | {r.Projects} | {r.Endpoints} | {r.ClientMethods} | " +
                          $"{Names(r.DependsOn)} | {Names(r.DependedOnBy)} |");
        sb.AppendLine();

        sb.AppendLine("## Service links").AppendLine();
        if (repos.Links.Count == 0) sb.AppendLine("No cross-repo package dependencies found.");
        foreach (var l in repos.Links)
            sb.AppendLine($"- {Name(l.From)} → {Name(l.To)} (via {string.Join(", ", l.ViaPackages)}; " +
                          $"{l.CallCount} call site{(l.CallCount == 1 ? "" : "s")}{(l.Confidence < Confidence.Certain ? $"; {l.Confidence.ToString().ToLowerInvariant()} confidence" : "")})");
        sb.AppendLine();

        sb.AppendLine("## Hotspots").AppendLine();
        foreach (var r in repos.Repos.Where(r => r.DependedOnBy.Count > 0)
                     .OrderByDescending(r => r.DependedOnBy.Count).ThenBy(r => r.Name, StringComparer.Ordinal).Take(5))
            sb.AppendLine($"- Repo **{r.Name}** is depended on by {r.DependedOnBy.Count} repo(s)");
        foreach (var e in q.FindEndpoints(null, limit: QueryService.MaxLimit).Items.Where(e => e.Callers > 0)
                     .OrderByDescending(e => e.Callers).ThenBy(e => e.Id, StringComparer.Ordinal).Take(5))
            sb.AppendLine($"- Endpoint **{e.Verb} {e.Route}** ({e.Repo}) is targeted by {e.Callers} client method(s)");
        sb.AppendLine();

        sb.AppendLine("## Diagnostics").AppendLine();
        var counts = q.GetDiagnostics(limit: 1).ByKind;
        if (counts.Count == 0) sb.AppendLine("None.");
        else
        {
            sb.AppendLine("| Kind | Count |").AppendLine("|---|---|");
            foreach (var c in counts) sb.AppendLine($"| {c.Kind} | {c.Count} |");
        }
        return sb.ToString();
    }

    private static string Name(string repoId) => repoId.StartsWith("repo:", StringComparison.Ordinal) ? repoId[5..] : repoId;
    private static string Names(List<string> ids) => ids.Count == 0 ? "—" : string.Join(", ", ids.Select(Name));
}
