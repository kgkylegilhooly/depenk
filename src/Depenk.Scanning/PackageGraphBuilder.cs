using Depenk.Core;
using Depenk.Core.Model;
using Depenk.Scanning.Config;

namespace Depenk.Scanning;

public sealed class ScannedProject(DiscoveredRepo repo, ProjectFile file)
{
    public DiscoveredRepo Repo { get; } = repo;
    public ProjectFile File { get; } = file;
    public string Id { get; } = Ids.Project(repo.Name, file.Name);
    public string Directory => Path.GetDirectoryName(File.AbsolutePath)!;
}

public static class PackageGraphBuilder
{
    public static List<ScannedProject> LoadProjects(string workspace, IEnumerable<DiscoveredRepo> repos,
        DepenkConfig config, DepGraph graph)
    {
        var result = new List<ScannedProject>();
        foreach (var repo in repos)
        foreach (var csproj in PathUtil.EnumerateFiles(repo.AbsolutePath, "*.csproj").Order(StringComparer.Ordinal))
        {
            if (Glob.Any(config.Projects.Ignore, Path.GetFileNameWithoutExtension(csproj))) continue;
            try
            {
                result.Add(new ScannedProject(repo, ProjectParser.Parse(csproj, repo.AbsolutePath)));
            }
            catch (Exception ex) when (ex is ProjectParseException or IOException or UnauthorizedAccessException)
            {
                var rel = PathUtil.Rel(workspace, csproj);
                graph.Diagnostics.Add(new Diagnostic(DiagnosticKinds.ParseError, "warning", [Ids.Repo(repo.Name)],
                    $"{rel}: {(ex.InnerException ?? ex).Message}"));
            }
        }
        return result;
    }

    public static void Build(string workspace, IReadOnlyList<ScannedProject> projects,
        IReadOnlyDictionary<string, ProjectKind> kinds, DepenkConfig config, DepGraph graph)
    {
        foreach (var p in projects)
            graph.Projects.Add(new ProjectNode(p.Id, p.Repo.Name, p.File.Name, PathUtil.Rel(workspace, p.File.AbsolutePath),
                kinds[p.Id], p.File.Sdk, p.File.IsPackable || p.File.ExplicitPackageId ? p.File.EffectivePackageId : null,
                p.File.Version, p.File.IsPackable));

        // candidate producers: every non-test project, keyed by effective package id
        var producersById = projects.Where(p => !p.File.IsTestProject)
            .GroupBy(p => p.File.EffectivePackageId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);

        var referencedIds = projects.SelectMany(p => p.File.PackageReferences.Select(r => r.Id))
            .Distinct(StringComparer.OrdinalIgnoreCase);
        // packages = everything referenced + everything explicitly packable
        var packageIds = referencedIds
            .Concat(projects.Where(p => p.File.IsPackable).Select(p => p.File.EffectivePackageId))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase);

        var producerRepoByPackage = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var id in packageIds)
        {
            var producers = producersById.TryGetValue(id, out var list) ? list : [];
            if (config.Packages.Producers.TryGetValue(id, out var pinnedRepo))
                producers = producers.Where(p => p.Repo.Name.Equals(pinnedRepo, StringComparison.OrdinalIgnoreCase)).ToList();

            var pkgId = Ids.Package(id);
            graph.Packages.Add(new PackageNode(pkgId, id, producers.Select(p => p.Id).ToList()));
            producerRepoByPackage[id] = producers.Select(p => p.Repo.Name).Distinct().ToList();

            var confidence = producers.Count > 1 ? Confidence.Low : Confidence.Certain;
            foreach (var prod in producers)
                graph.Edges.Add(new Edge(EdgeKind.Produces, prod.Id, pkgId, confidence) { Version = prod.File.Version });
            if (producers.Count > 1)
                graph.Diagnostics.Add(new Diagnostic(DiagnosticKinds.AmbiguousProducer, "warning",
                    [pkgId, .. producers.Select(p => p.Id)],
                    $"Package {id} is produced by {producers.Count} projects: {string.Join(", ", producers.Select(p => p.Id))}. Pin one with packages.producers in depenk.yml."));
        }

        var dependsOn = new Dictionary<(string From, string To), SortedSet<string>>();
        foreach (var p in projects)
        foreach (var r in p.File.PackageReferences)
        {
            graph.Edges.Add(new Edge(EdgeKind.References, p.Id, Ids.Package(r.Id), Confidence.Certain) { Version = r.Version });
            if (ProjectFile.IsUnresolved(r.Version))
                graph.Diagnostics.Add(new Diagnostic(DiagnosticKinds.UnresolvedVersion, "info", [p.Id, Ids.Package(r.Id)],
                    $"{p.Id} references {r.Id} with a version that could not be resolved: {r.Version}"));

            foreach (var producerRepo in producerRepoByPackage.GetValueOrDefault(r.Id, []))
            {
                if (producerRepo == p.Repo.Name) continue;
                var key = (Ids.Repo(p.Repo.Name), Ids.Repo(producerRepo));
                if (!dependsOn.TryGetValue(key, out var via)) dependsOn[key] = via = new SortedSet<string>(StringComparer.Ordinal);
                via.Add(r.Id);
            }
        }
        foreach (var ((from, to), via) in dependsOn.OrderBy(k => k.Key.From).ThenBy(k => k.Key.To))
            graph.Edges.Add(new Edge(EdgeKind.DependsOn, from, to, Confidence.Certain) { ViaPackages = [.. via] });
    }
}
