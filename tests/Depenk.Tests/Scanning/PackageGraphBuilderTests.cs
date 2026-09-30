using Depenk.Core;
using Depenk.Core.Model;
using Depenk.Scanning;
using Depenk.Scanning.Config;
using Depenk.Tests.TestUtil;

namespace Depenk.Tests.Scanning;

public class PackageGraphBuilderTests
{
    private static string Csproj(string sdk, string props = "", string refs = "") =>
        $"<Project Sdk=\"{sdk}\"><PropertyGroup>{props}</PropertyGroup><ItemGroup>{refs}</ItemGroup></Project>";

    private static (DepGraph Graph, List<ScannedProject> Projects) Run(TempWorkspace ws, DepenkConfig? cfg = null)
    {
        cfg ??= new DepenkConfig();
        var g = new DepGraph();
        var repos = RepoDiscovery.Discover(ws.Root, cfg);
        g.Repos.AddRange(repos.Select(r => new RepoNode(Ids.Repo(r.Name), r.Name, r.RelativePath, r.HeadSha, r.Dirty)));
        var projects = PackageGraphBuilder.LoadProjects(ws.Root, repos, cfg, g);
        var kinds = projects.ToDictionary(p => p.Id,
            p => ProjectClassifier.Classify(p.File, new ProjectSignals(false, p.File.Name.EndsWith(".Client")), cfg));
        PackageGraphBuilder.Build(ws.Root, projects, kinds, cfg, g);
        return (g, projects);
    }

    [Fact]
    public void LinksConsumerToProducer_AndDerivesRepoDependency()
    {
        using var ws = new TempWorkspace().Repo("orders").Repo("billing")
            .File("orders/src/Orders.Client/Orders.Client.csproj",
                Csproj("Microsoft.NET.Sdk", "<IsPackable>true</IsPackable><Version>3.4.1</Version>"))
            .File("billing/src/Billing.Api/Billing.Api.csproj",
                Csproj("Microsoft.NET.Sdk.Web", "", "<PackageReference Include=\"Orders.Client\" Version=\"3.2.0\" /><PackageReference Include=\"Serilog\" Version=\"4.0.0\" />"));

        var (g, _) = Run(ws);

        Assert.Contains(g.Projects, p => p.Id == "proj:orders/Orders.Client" && p.Kind == ProjectKind.Client
                                         && p.Path == "orders/src/Orders.Client/Orders.Client.csproj");
        var pkg = g.Packages.Single(p => p.PackageId == "Orders.Client");
        Assert.Equal(["proj:orders/Orders.Client"], pkg.ProducerProjectIds);
        Assert.True(g.Packages.Single(p => p.PackageId == "Serilog").External);

        var refEdge = g.EdgesOf(EdgeKind.References).Single(e => e.To == "pkg:Orders.Client");
        Assert.Equal(("proj:billing/Billing.Api", "3.2.0", Confidence.Certain), (refEdge.From, refEdge.Version, refEdge.Confidence));
        var produces = g.EdgesOf(EdgeKind.Produces).Single();
        Assert.Equal(("proj:orders/Orders.Client", "pkg:Orders.Client", "3.4.1"), (produces.From, produces.To, produces.Version));

        var dep = g.EdgesOf(EdgeKind.DependsOn).Single();
        Assert.Equal(("repo:billing", "repo:orders"), (dep.From, dep.To));
        Assert.Equal(["Orders.Client"], dep.ViaPackages);
    }

    [Fact]
    public void DuplicateProducers_AreKeptAsLowConfidence_WithDiagnostic()
    {
        using var ws = new TempWorkspace().Repo("a").Repo("b").Repo("c")
            .File("a/Shared/Shared.csproj", Csproj("Microsoft.NET.Sdk", "<PackageId>Acme.Shared</PackageId>"))
            .File("b/Shared/Shared.csproj", Csproj("Microsoft.NET.Sdk", "<PackageId>Acme.Shared</PackageId>"))
            .File("c/App/App.csproj", Csproj("Microsoft.NET.Sdk", "", "<PackageReference Include=\"Acme.Shared\" Version=\"1.0.0\" />"));

        var (g, _) = Run(ws);

        Assert.Equal(2, g.Packages.Single(p => p.PackageId == "Acme.Shared").ProducerProjectIds.Count);
        Assert.All(g.EdgesOf(EdgeKind.Produces), e => Assert.Equal(Confidence.Low, e.Confidence));
        Assert.Contains(g.Diagnostics, d => d.Kind == DiagnosticKinds.AmbiguousProducer && d.NodeIds.Contains("pkg:Acme.Shared"));
    }

    [Fact]
    public void ConfigProducerPin_ResolvesAmbiguity()
    {
        using var ws = new TempWorkspace().Repo("a").Repo("b")
            .File("a/Shared/Shared.csproj", Csproj("Microsoft.NET.Sdk", "<PackageId>Acme.Shared</PackageId>"))
            .File("b/Shared/Shared.csproj", Csproj("Microsoft.NET.Sdk", "<PackageId>Acme.Shared</PackageId>"));
        var cfg = new DepenkConfig();
        cfg.Packages.Producers["Acme.Shared"] = "b";

        var (g, _) = Run(ws, cfg);

        Assert.Equal(["proj:b/Shared"], g.Packages.Single().ProducerProjectIds);
        Assert.DoesNotContain(g.Diagnostics, d => d.Kind == DiagnosticKinds.AmbiguousProducer);
    }

    [Fact]
    public void BadCsproj_BecomesParseErrorDiagnostic_AndScanContinues()
    {
        using var ws = new TempWorkspace().Repo("r")
            .File("r/Bad/Bad.csproj", "<Project><oops></Project>")
            .File("r/Good/Good.csproj", Csproj("Microsoft.NET.Sdk"));

        var (g, projects) = Run(ws);

        Assert.Equal(["Good"], projects.Select(p => p.File.Name));
        var diag = g.Diagnostics.Single(d => d.Kind == DiagnosticKinds.ParseError);
        Assert.Contains("r/Bad/Bad.csproj", diag.Message);
        Assert.DoesNotContain(":\\", diag.Message); // no absolute Windows paths leak
    }

    [Fact]
    public void UnresolvedVersion_EmitsDiagnostic()
    {
        using var ws = new TempWorkspace().Repo("r")
            .File("r/App/App.csproj", Csproj("Microsoft.NET.Sdk", "", "<PackageReference Include=\"X\" Version=\"$(Nope)\" />"));
        var (g, _) = Run(ws);
        Assert.Equal("unresolved($(Nope))", g.EdgesOf(EdgeKind.References).Single().Version);
        Assert.Contains(g.Diagnostics, d => d.Kind == DiagnosticKinds.UnresolvedVersion);
    }

    [Fact]
    public void IgnoredProjects_AreSkipped()
    {
        using var ws = new TempWorkspace().Repo("r")
            .File("r/App.Benchmarks/App.Benchmarks.csproj", Csproj("Microsoft.NET.Sdk"))
            .File("r/App/App.csproj", Csproj("Microsoft.NET.Sdk"));
        var cfg = new DepenkConfig();
        cfg.Projects.Ignore.Add("*.Benchmarks");
        var (_, projects) = Run(ws, cfg);
        Assert.Equal(["App"], projects.Select(p => p.File.Name));
    }
}
