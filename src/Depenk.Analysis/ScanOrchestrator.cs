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

    public ScanOrchestrator(ParseCache? cache = null) => _cache = cache ?? new ParseCache();

    public DepGraph Scan(string workspace)
    {
        workspace = Path.GetFullPath(workspace);
        var config = ConfigLoader.Load(workspace);
        var g = new DepGraph { Workspace = "." };

        var repos = RepoDiscovery.Discover(workspace, config);
        g.Repos.AddRange(repos.Select(r => new RepoNode(Ids.Repo(r.Name), r.Name, r.RelativePath, r.HeadSha, r.Dirty)));
        var projects = LoadProjects(workspace, repos, config, g);

        var loaded = new (SourceSet Set, List<Diagnostic> Diags)[projects.Count];
        Parallel.For(0, projects.Count, i =>
        {
            var local = new DepGraph();
            loaded[i] = (LoadSources(workspace, projects[i], projects, local, _cache), local.Diagnostics);
        });
        var sources = new Dictionary<string, SourceSet>();
        for (var i = 0; i < projects.Count; i++)
        {
            sources[projects[i].Id] = loaded[i].Set;
            g.Diagnostics.AddRange(loaded[i].Diags);
        }
        var configPath = Path.Combine(workspace, ConfigLoader.FileName);
        var configHash = File.Exists(configPath) ? ParseCache.HashText(File.ReadAllText(configPath)) : "";

        IEndpointFinder[] endpointFinders = [new ControllerEndpointFinder(), new MinimalApiEndpointFinder()];
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
                var test = p.File.IsTestProject;
                cached = (key,
                    test ? [] : endpointFinders.SelectMany(f => f.Find(src)).ToList(),
                    test ? new ClientScanResult([], false) : clientFinder.Find(src));
                _analysis[p.Id] = cached;
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
        new ModelExtractor([.. sources.Values], RefsOf)
            .Extract(g, kinds.Where(k => k.Value == ProjectKind.Client).Select(k => k.Key).ToHashSet(),
                g.ClientMethods.Select(cm => (cm.ProjectId, cm.TypeName)).ToHashSet());

        var usedSiteIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var p in projects.Where(p => !p.File.IsTestProject))
        {
            var refs = RefsOf(p.Id).ToHashSet();
            var scan = CallSiteFinder.Find(sources[p.Id], g.ClientMethods.Where(cm => refs.Contains(cm.ProjectId)).ToList());
            AddCallSiteScan(g, scan, usedSiteIds);
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
            try { result.AddRange(PackageGraphBuilder.LoadProjects(workspace, [repo], config, g)); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                ParseError(g, workspace, repo.Name, "warning", $"{PathUtil.Rel(workspace, repo.AbsolutePath)}: {ex.Message}");
            }
        }
        return result;
    }

    private static SourceSet LoadSources(string workspace, ScannedProject p, IReadOnlyList<ScannedProject> all, DepGraph g, ParseCache cache)
    {
        SourceSet set;
        try
        {
            set = SourceSet.Load(workspace, p.Repo.Name, p.Id, p.File.Name, p.Directory,
                (rel, ex) => ParseError(g, workspace, p.Repo.Name, "info", $"{rel}: {ex.Message}"), cache);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ParseError(g, workspace, p.Repo.Name, "warning", $"{PathUtil.Rel(workspace, p.Directory)}: {ex.Message}");
            return new SourceSet(p.Repo.Name, p.Id, p.File.Name, []);
        }
        var nested = all.Where(o => o != p && IsUnder(o.Directory, p.Directory))
            .Select(o => PathUtil.Rel(workspace, o.Directory) + "/").ToList();
        var docs = set.Docs.Where(d => !nested.Any(n => d.RelativePath.StartsWith(n, StringComparison.Ordinal))).ToList();
        foreach (var doc in docs)
        {
            var err = doc.Tree.GetDiagnostics().FirstOrDefault(d => d.Severity == DiagnosticSeverity.Error);
            if (err is not null)
                ParseError(g, workspace, p.Repo.Name, "info",
                    $"{doc.RelativePath}: line {err.Location.GetLineSpan().StartLinePosition.Line + 1}: {err.GetMessage()}");
        }
        return new SourceSet(set.Repo, set.ProjectId, set.ProjectName, docs);
    }

    private static bool IsUnder(string dir, string parent) =>
        dir.Length > parent.Length
        && dir.StartsWith(parent.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    private static void ParseError(DepGraph g, string workspace, string repo, string severity, string message) =>
        g.Diagnostics.Add(new Diagnostic(DiagnosticKinds.ParseError, severity, [Ids.Repo(repo)], StripWorkspace(workspace, message)));

    // Exception messages embed absolute paths; the graph must stay workspace-relative.
    private static string StripWorkspace(string workspace, string message)
    {
        var root = workspace.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return message
            .Replace(root + Path.DirectorySeparatorChar, "", StringComparison.OrdinalIgnoreCase)
            .Replace(root.Replace('\\', '/') + "/", "", StringComparison.OrdinalIgnoreCase);
    }

    private static void DedupeEndpointIds(DepGraph g)
    {
        var seen = new Dictionary<string, int>();
        for (var i = 0; i < g.Endpoints.Count; i++)
        {
            var id = g.Endpoints[i].Id;
            seen[id] = seen.GetValueOrDefault(id) + 1;
            if (seen[id] > 1) g.Endpoints[i] = g.Endpoints[i] with { Id = $"{id}#{seen[id]}" };
        }
    }

    // Two Client projects can share a name (same file name in different repos, or #2 duplicates), so cm ids may collide.
    private static void DedupeClientMethodIds(DepGraph g)
    {
        var seen = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < g.ClientMethods.Count; i++)
        {
            var id = g.ClientMethods[i].Id;
            seen[id] = seen.GetValueOrDefault(id) + 1;
            if (seen[id] > 1) g.ClientMethods[i] = g.ClientMethods[i] with { Id = $"{id}#{seen[id]}" };
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
        var byPath = projects.ToDictionary(p => p.File.AbsolutePath, p => p.Id, StringComparer.OrdinalIgnoreCase);
        var producers = g.Packages.ToDictionary(p => p.PackageId, p => p.ProducerProjectIds, StringComparer.OrdinalIgnoreCase);
        return projects.ToDictionary(p => p.Id, p => (IReadOnlyList<string>)
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
        var diags = g.Diagnostics.OrderBy(d => d.Kind, StringComparer.Ordinal)
            .ThenBy(d => d.NodeIds.FirstOrDefault(), StringComparer.Ordinal).ThenBy(d => d.Message, StringComparer.Ordinal).ToList();
        g.Diagnostics.Clear(); g.Diagnostics.AddRange(diags);
    }
}
