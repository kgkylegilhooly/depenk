using Depenk.Core;
using Depenk.Core.Model;
using Depenk.Scanning.Config;

namespace Depenk.Scanning;

public sealed class ScannedProject(DiscoveredRepo repo, ProjectFile file, string? id = null)
{
    public DiscoveredRepo Repo { get; } = repo;
    public ProjectFile File { get; } = file;
    public string Id { get; } = id ?? Ids.Project(repo.Name, file.Name);
    public string Directory => Path.GetDirectoryName(File.AbsolutePath)!;
}

public static class PackageGraphBuilder
{
    public static List<ScannedProject> LoadProjects(string workspace, IEnumerable<DiscoveredRepo> repos,
        DepenkConfig config, DepGraph graph)
    {
        var result = new List<ScannedProject>();
        var usedIds = new Dictionary<string, (ScannedProject Project, string RelPath)>(StringComparer.Ordinal);
        var duplicates = new List<(string Id, string RelPath, string OtherRelPath)>();

        foreach (var repo in repos)
        foreach (var csproj in PathUtil.EnumerateFiles(repo.AbsolutePath, "*.csproj").Order(StringComparer.Ordinal))
        {
            if (Glob.Any(config.Projects.Ignore, Path.GetFileNameWithoutExtension(csproj))) continue;
            try
            {
                var pf = ProjectParser.Parse(csproj, repo.AbsolutePath);
                var baseId = Ids.Project(repo.Name, pf.Name);
                var id = baseId;
                var suffix = 2;

                // Check for duplicate project IDs within the same repo
                while (usedIds.ContainsKey(id))
                {
                    id = Ids.Project(repo.Name, pf.Name) + $"#{suffix}";
                    suffix++;
                }

                var sp = new ScannedProject(repo, pf, id);
                result.Add(sp);
                var rel = PathUtil.Rel(workspace, csproj);
                usedIds[id] = (sp, rel);

                // Track for duplicate diagnostic
                if (id != baseId)
                {
                    var (_, otherRel) = usedIds[baseId];
                    duplicates.Add((baseId, rel, otherRel));
                }
            }
            catch (Exception ex) when (ex is ProjectParseException or IOException or UnauthorizedAccessException)
            {
                var rel = PathUtil.Rel(workspace, csproj);
                graph.Diagnostics.Add(new Diagnostic(DiagnosticKinds.ParseError, "warning", [Ids.Repo(repo.Name)],
                    $"{rel}: {(ex.InnerException ?? ex).Message}"));
            }
        }

        // Add duplicate diagnostics
        foreach (var (baseId, rel, otherRel) in duplicates)
        {
            var suffixedId = baseId + "#2";
            graph.Diagnostics.Add(new Diagnostic(DiagnosticKinds.DuplicateProjectName, "info",
                [baseId, suffixedId],
                $"Duplicate project name: {otherRel} and {rel} both produce project ID {baseId}; renamed to {suffixedId}."));
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

        // Build canonical packageId map: use producer's casing when available, otherwise the ID as-is
        var canonicalPackageId = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var producerRepoByPackage = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        var packageIsAmbiguous = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);

        foreach (var id in packageIds)
        {
            var producers = producersById.TryGetValue(id, out var list) ? list : [];
            if (config.Packages.Producers.TryGetValue(id, out var pinnedRepo))
                producers = producers.Where(p => p.Repo.Name.Equals(pinnedRepo, StringComparison.OrdinalIgnoreCase)).ToList();

            // Canonical casing: prefer producer's casing
            var canonical = producers.Count > 0 ? producers[0].File.EffectivePackageId : id;
            canonicalPackageId[id] = canonical;
            packageIsAmbiguous[canonical] = producers.Count > 1;

            var pkgId = Ids.Package(canonical);
            graph.Packages.Add(new PackageNode(pkgId, canonical, producers.Select(p => p.Id).ToList()));
            producerRepoByPackage[canonical] = producers.Select(p => p.Repo.Name).Distinct().ToList();

            var confidence = producers.Count > 1 ? Confidence.Low : Confidence.Certain;
            foreach (var prod in producers)
                graph.Edges.Add(new Edge(EdgeKind.Produces, prod.Id, pkgId, confidence) { Version = prod.File.Version });
            if (producers.Count > 1)
                graph.Diagnostics.Add(new Diagnostic(DiagnosticKinds.AmbiguousProducer, "warning",
                    [pkgId, .. producers.Select(p => p.Id)],
                    $"Package {canonical} is produced by {producers.Count} projects: {string.Join(", ", producers.Select(p => p.Id))}. Pin one with packages.producers in depenk.yml."));
        }

        var dependsOnData = new Dictionary<(string From, string To), (SortedSet<string> Packages, bool HasAmbiguous)>();
        foreach (var p in projects)
        foreach (var r in p.File.PackageReferences)
        {
            var canonical = canonicalPackageId.TryGetValue(r.Id, out var c) ? c : r.Id;
            graph.Edges.Add(new Edge(EdgeKind.References, p.Id, Ids.Package(canonical), Confidence.Certain) { Version = r.Version });
            if (ProjectFile.IsUnresolved(r.Version))
                graph.Diagnostics.Add(new Diagnostic(DiagnosticKinds.UnresolvedVersion, "info", [p.Id, Ids.Package(canonical)],
                    $"{p.Id} references {r.Id} with a version that could not be resolved: {r.Version}"));

            foreach (var producerRepo in producerRepoByPackage.GetValueOrDefault(canonical, []))
            {
                if (producerRepo.Equals(p.Repo.Name, StringComparison.OrdinalIgnoreCase)) continue;
                var key = (Ids.Repo(p.Repo.Name), Ids.Repo(producerRepo));
                if (!dependsOnData.ContainsKey(key))
                    dependsOnData[key] = (new SortedSet<string>(StringComparer.Ordinal), false);

                var (packages, hasAmbiguous) = dependsOnData[key];
                packages.Add(canonical);
                if (packageIsAmbiguous.GetValueOrDefault(canonical, false))
                    hasAmbiguous = true;
                dependsOnData[key] = (packages, hasAmbiguous);
            }
        }
        foreach (var ((from, to), (via, hasAmbiguous)) in dependsOnData.OrderBy(k => k.Key.From).ThenBy(k => k.Key.To))
        {
            var confidence = hasAmbiguous ? Confidence.Low : Confidence.Certain;
            graph.Edges.Add(new Edge(EdgeKind.DependsOn, from, to, confidence) { ViaPackages = [.. via] });
        }
    }
}
