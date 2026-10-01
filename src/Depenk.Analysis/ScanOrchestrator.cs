using Depenk.Analysis.CallSites;
using Depenk.Analysis.Clients;
using Depenk.Analysis.Diagnostics;
using Depenk.Analysis.Endpoints;
using Depenk.Analysis.Linking;
using Depenk.Analysis.Models;
using Depenk.Core;
using Depenk.Core.Model;
using Depenk.Scanning;
using Depenk.Scanning.Config;
using Microsoft.CodeAnalysis;
using Diagnostic = Depenk.Core.Model.Diagnostic;

namespace Depenk.Analysis;

public sealed class ScanOrchestrator
{
    public static string GraphPath(string workspace) => Path.Combine(workspace, ".depenk", "graph.json");

    private readonly ParseCache _cache;
    private readonly Dictionary<string, (string Key, List<EndpointNode> Endpoints, ClientScanResult Clients)> _analysis = [];

    /// <summary>Cumulative number of project analyses actually computed (cache misses).</summary>
    public int AnalyzedProjectCount { get; private set; }

    private readonly IEndpointFinder[] _endpointFinders;

    public ScanOrchestrator(ParseCache? cache = null) : this(cache, null) { }

    /// <summary>Test seam: custom endpoint finders.</summary>
    internal ScanOrchestrator(ParseCache? cache, IEndpointFinder[]? endpointFinders)
    {
        _cache = cache ?? new ParseCache();
        _endpointFinders = endpointFinders ?? [new ControllerEndpointFinder(), new MinimalApiEndpointFinder()];
    }

    /// <summary>Test seam: called with (stage, projectId) before each isolated per-project step ("analysis", "callSites").</summary>
    internal Action<string, string>? FaultInjection { get; init; }

    public DepGraph Scan(string workspace)
    {
        workspace = Path.GetFullPath(workspace);
        var config = ConfigLoader.Load(workspace);
        var g = new DepGraph { Workspace = "." };

        var repos = RepoDiscovery.Discover(workspace, config);
        g.Repos.AddRange(repos.Select(r => new RepoNode(Ids.Repo(r.Name), r.Name, r.RelativePath, r.HeadSha, r.Dirty)));
        foreach (var renamed in repos.Where(r => r.Name != r.BaseName))
        {
            var original = repos.First(r => r.Name == r.BaseName && r.Name.Equals(renamed.BaseName, StringComparison.OrdinalIgnoreCase));
            g.Diagnostics.Add(new Diagnostic(DiagnosticKinds.DuplicateRepoName, Severities.Info,
                [Ids.Repo(original.Name), Ids.Repo(renamed.Name)],
                $"Duplicate repo name: {original.RelativePath} and {renamed.RelativePath} are both named '{renamed.BaseName}'; " +
                $"{renamed.RelativePath} is scanned as '{renamed.Name}'."));
        }
        var projects = LoadProjects(workspace, repos, config, g);

        var repoDirs = repos.Select(r => r.AbsolutePath).ToList();
        var loaded = new (SourceSet Set, List<Diagnostic> Diags)[projects.Count];
        Parallel.For(0, projects.Count, i =>
        {
            var local = new DepGraph();
            loaded[i] = (LoadSources(workspace, projects[i], projects, repoDirs, local, _cache), local.Diagnostics);
        });
        var sources = new Dictionary<string, SourceSet>();
        for (var i = 0; i < projects.Count; i++)
        {
            sources[projects[i].Id] = loaded[i].Set;
            g.Diagnostics.AddRange(loaded[i].Diags);
        }
        var configPath = Path.Combine(workspace, ConfigLoader.FileName);
        var configHash = File.Exists(configPath) ? ParseCache.HashText(File.ReadAllText(configPath)) : "";

        var clientFinder = new ClientMethodFinder(config);
        var endpoints = new Dictionary<string, List<EndpointNode>>();
        var clients = new Dictionary<string, ClientScanResult>();
        var kinds = new Dictionary<string, ProjectKind>();
        foreach (var p in projects)
        {
            var src = sources[p.Id];
            var key = configHash + "|" + p.File.IsTestProject + "|" + string.Join(",", src.Docs.Select(d => d.RelativePath + ":" + d.Hash));
            if (!_analysis.TryGetValue(p.Id, out var cached) || cached.Key != key)
            {
                AnalyzedProjectCount++;
                try
                {
                    FaultInjection?.Invoke("analysis", p.Id);
                    var test = p.File.IsTestProject;
                    cached = (key,
                        test ? [] : _endpointFinders.SelectMany(f => f.Find(src)).ToList(),
                        test ? new ClientScanResult([], false) : clientFinder.Find(src));
                    _analysis[p.Id] = cached;
                }
                catch (Exception ex)
                {
                    ProjectError(g, workspace, p, ex); // not cached: the next scan retries
                    _analysis.Remove(p.Id);
                    cached = (key, [], new ClientScanResult([], false));
                }
            }
            endpoints[p.Id] = cached.Endpoints;
            clients[p.Id] = cached.Clients;
            kinds[p.Id] = ProjectClassifier.Classify(p.File,
                new ProjectSignals(cached.Endpoints.Count > 0, cached.Clients.AnyHit), config);
        }
        PackageGraphBuilder.Build(workspace, projects, kinds, config, g);

        foreach (var p in projects)
        {
            if (kinds[p.Id] == ProjectKind.Api) g.Endpoints.AddRange(endpoints[p.Id]);
            if (kinds[p.Id] == ProjectKind.Client) g.ClientMethods.AddRange(clients[p.Id].Methods);
        }
        DedupeEndpointIds(g);
        DedupeClientMethodIds(g);
        ClientEndpointLinker.Link(g);

        var referenced = ReferencedProjects(projects, g);
        IReadOnlyList<string> RefsOf(string id) => referenced.GetValueOrDefault(id, []);
        var byId = projects.GroupBy(p => p.Id).ToDictionary(x => x.Key, x => x.First());
        try
        {
            new ModelExtractor([.. sources.Values], RefsOf)
                .Extract(g, kinds.Where(k => k.Value == ProjectKind.Client).Select(k => k.Key).ToHashSet(),
                    g.ClientMethods.Select(cm => (cm.ProjectId, cm.TypeName)).ToHashSet(),
                    (projectId, ex) => { if (byId.TryGetValue(projectId, out var p)) ProjectError(g, workspace, p, ex); });
        }
        catch (Exception ex) // outside any one project (e.g. building the type index): models stay partial
        {
            g.Diagnostics.Add(new Diagnostic(DiagnosticKinds.ParseError, Severities.Warning, [],
                PathUtil.StripWorkspace(workspace, $"model extraction failed: {ex.Message}")));
        }

        var usedSiteIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var p in projects.Where(p => !p.File.IsTestProject))
        {
            try
            {
                FaultInjection?.Invoke("callSites", p.Id);
                var refs = RefsOf(p.Id).ToHashSet();
                var scan = CallSiteFinder.Find(sources[p.Id], g.ClientMethods.Where(cm => refs.Contains(cm.ProjectId)).ToList());
                AddCallSiteScan(g, scan, usedSiteIds);
            }
            catch (Exception ex) { ProjectError(g, workspace, p, ex); }
        }

        FillCallCounts(g);
        GraphDiagnostics.Add(g);
        Sort(g);
        return g;
    }

    private static List<ScannedProject> LoadProjects(string workspace, IReadOnlyList<DiscoveredRepo> repos, DepenkConfig config, DepGraph g)
    {
        var result = new List<ScannedProject>();
        foreach (var repo in repos)
        {
            // One repo failing for any reason must not discard the others.
            try { result.AddRange(PackageGraphBuilder.LoadProjects(workspace, [repo], config, g, repos)); }
            catch (Exception ex)
            {
                ParseError(g, workspace, repo.Name, Severities.Warning, $"{repo.RelativePath}: {ex.Message}");
            }
        }
        return result;
    }

    private static SourceSet LoadSources(string workspace, ScannedProject p, IReadOnlyList<ScannedProject> all,
        IReadOnlyList<string> repoDirs, DepGraph g, ParseCache cache)
    {
        SourceSet set;
        try
        {
            set = SourceSet.Load(workspace, p.Repo.Name, p.Id, p.File.Name, p.Directory,
                (rel, ex) => ParseError(g, workspace, p.Repo.Name, Severities.Info, $"{rel}: {ex.Message}"), cache,
                (dir, ex) => ParseError(g, workspace, p.Repo.Name, Severities.Warning,
                    $"{PathUtil.Rel(workspace, dir)}/: directory skipped: {ex.Message}"));
        }
        catch (Exception ex)
        {
            ParseError(g, workspace, p.Repo.Name, Severities.Warning, $"{PathUtil.Rel(workspace, p.Directory)}: {ex.Message}");
            return new SourceSet(p.Repo.Name, p.Id, p.File.Name, []);
        }
        // Sources of nested projects and nested repos belong to them, not to this project.
        var nested = all.Where(o => o != p && IsUnder(o.Directory, p.Directory)).Select(o => o.Directory)
            .Concat(repoDirs.Where(d => IsUnder(d, p.Directory)))
            .Select(d => PathUtil.Rel(workspace, d) + "/").ToList();
        var docs = set.Docs.Where(d => !nested.Any(n => d.RelativePath.StartsWith(n, StringComparison.Ordinal))).ToList();
        foreach (var doc in docs)
        {
            var err = doc.Tree.GetDiagnostics().FirstOrDefault(d => d.Severity == DiagnosticSeverity.Error);
            if (err is not null)
                ParseError(g, workspace, p.Repo.Name, Severities.Info,
                    $"{doc.RelativePath}: line {err.Location.GetLineSpan().StartLinePosition.Line + 1}: {err.GetMessage()}");
        }
        return new SourceSet(set.Repo, set.ProjectId, set.ProjectName, docs);
    }

    private static bool IsUnder(string dir, string parent) =>
        dir.Length > parent.Length
        && dir.StartsWith(parent.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    private static void ParseError(DepGraph g, string workspace, string repo, string severity, string message) =>
        g.Diagnostics.Add(new Diagnostic(DiagnosticKinds.ParseError, severity, [Ids.Repo(repo)], PathUtil.StripWorkspace(workspace, message)));

    /// <summary>An unexpected failure while analysing one project: a parseError for its repo, and the scan goes on.</summary>
    private static void ProjectError(DepGraph g, string workspace, ScannedProject p, Exception ex) =>
        ParseError(g, workspace, p.Repo.Name, Severities.Warning,
            $"{PathUtil.Rel(workspace, p.File.AbsolutePath)}: analysis failed: {ex.Message}");

    private static void DedupeEndpointIds(DepGraph g) =>
        DedupeIds(g.Endpoints, e => e.Id, (e, id) => e with { Id = id });

    // Two Client projects can share a name (same file name in different repos, or #2 duplicates), so cm ids may collide.
    private static void DedupeClientMethodIds(DepGraph g) =>
        DedupeIds(g.ClientMethods, c => c.Id, (c, id) => c with { Id = id });

    /// <summary>
    /// The first holder of an id keeps it; later ones get the lowest free "#N". A generated id never takes an id
    /// that some other item already carries (finders may emit "#2" themselves, e.g. for overloads).
    /// </summary>
    private static void DedupeIds<T>(List<T> items, Func<T, string> idOf, Func<T, string, T> withId)
    {
        var taken = items.Select(idOf).ToHashSet(StringComparer.Ordinal);
        var kept = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < items.Count; i++)
        {
            var id = idOf(items[i]);
            if (kept.Add(id)) continue;
            var n = 2;
            while (taken.Contains($"{id}#{n}")) n++;
            var fresh = $"{id}#{n}";
            taken.Add(fresh);
            kept.Add(fresh);
            items[i] = withId(items[i], fresh);
        }
    }

    // Call site ids embed repo/project name only, so projects sharing a name can collide.
    // Rename and rewrite this scan's own references consistently.
    private static void AddCallSiteScan(DepGraph g, CallSiteScan scan, HashSet<string> used)
    {
        var renames = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var site in scan.CallSites)
        {
            var id = site.Id;
            for (var n = 2; used.Contains(id); n++) id = $"{site.Id}#{n}";
            used.Add(id);
            if (id != site.Id) renames[site.Id] = id;
            g.CallSites.Add(id == site.Id ? site : site with { Id = id });
        }
        string Map(string id) => renames.GetValueOrDefault(id, id);
        g.Edges.AddRange(scan.Invokes.Select(e => e with { From = Map(e.From) }));
        g.Diagnostics.AddRange(scan.Diagnostics.Select(d => d with { NodeIds = d.NodeIds.Select(Map).ToList() }));
    }

    private static Dictionary<string, IReadOnlyList<string>> ReferencedProjects(IReadOnlyList<ScannedProject> projects, DepGraph g)
    {
        var byPath = projects.GroupBy(p => p.File.AbsolutePath, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(x => x.Key, x => x.First().Id, StringComparer.OrdinalIgnoreCase);
        var producers = g.Packages.GroupBy(p => p.PackageId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(x => x.Key, x => x.SelectMany(p => p.ProducerProjectIds).Distinct().ToList(), StringComparer.OrdinalIgnoreCase);
        return projects.GroupBy(p => p.Id).Select(x => x.First()).ToDictionary(p => p.Id, p => (IReadOnlyList<string>)
            p.File.ProjectReferences.Select(r => byPath.GetValueOrDefault(r)).OfType<string>()
                .Concat(p.File.PackageReferences.SelectMany(r => producers.GetValueOrDefault(r.Id, [])))
                .Distinct().ToList());
    }

    private static void FillCallCounts(DepGraph g)
    {
        var siteRepo = g.CallSites.GroupBy(c => c.Id).ToDictionary(x => x.Key, x => x.First().Repo);
        var methodRepo = g.ClientMethods.GroupBy(c => c.Id).ToDictionary(x => x.Key, x => x.First().Repo);
        var counts = g.EdgesOf(EdgeKind.Invokes)
            .Where(e => siteRepo.ContainsKey(e.From) && methodRepo.ContainsKey(e.To))
            .GroupBy(e => (From: Ids.Repo(siteRepo[e.From]), To: Ids.Repo(methodRepo[e.To])))
            .ToDictionary(x => x.Key, x => x.Count());
        for (var i = 0; i < g.Edges.Count; i++)
            if (g.Edges[i].Kind == EdgeKind.DependsOn)
                g.Edges[i] = g.Edges[i] with { CallCount = counts.GetValueOrDefault((g.Edges[i].From, g.Edges[i].To)) };
    }

    private static void Sort(DepGraph g)
    {
        static int ById<T>(T a, T b, Func<T, string> id) => string.CompareOrdinal(id(a), id(b));
        g.Repos.Sort((a, b) => ById(a, b, x => x.Id));
        g.Projects.Sort((a, b) => ById(a, b, x => x.Id));
        g.Packages.Sort((a, b) => ById(a, b, x => x.Id));
        g.Endpoints.Sort((a, b) => ById(a, b, x => x.Id));
        g.ClientMethods.Sort((a, b) => ById(a, b, x => x.Id));
        g.CallSites.Sort((a, b) => ById(a, b, x => x.Id));
        g.Models.Sort((a, b) => ById(a, b, x => x.Id));
        var edges = g.Edges.OrderBy(e => e.Kind).ThenBy(e => e.From, StringComparer.Ordinal).ThenBy(e => e.To, StringComparer.Ordinal)
            .ThenBy(e => e.Source, StringComparer.Ordinal).ThenBy(e => e.StatusCode).ThenBy(e => e.FieldName, StringComparer.Ordinal).ToList();
        g.Edges.Clear(); g.Edges.AddRange(edges);
        // identical diagnostics (e.g. one skipped folder seen by both the project and the source enumeration) collapse
        var diags = g.Diagnostics.DistinctBy(d => (d.Kind, d.Severity, string.Join('\n', d.NodeIds), d.Message))
            .OrderBy(d => d.Kind, StringComparer.Ordinal)
            .ThenBy(d => d.NodeIds.FirstOrDefault(), StringComparer.Ordinal).ThenBy(d => d.Message, StringComparer.Ordinal).ToList();
        g.Diagnostics.Clear(); g.Diagnostics.AddRange(diags);
    }
}
