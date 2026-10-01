using System.Diagnostics;
using Depenk.Analysis;
using Depenk.Analysis.Endpoints;
using Depenk.Core.Model;
using Depenk.Tests.TestUtil;

namespace Depenk.Tests;

/// <summary>Partial results beat failed scans; repo-name collisions never crash the scan.</summary>
public class ScanRobustnessTests
{
    private const string WebCsproj = "<Project Sdk=\"Microsoft.NET.Sdk.Web\" />";

    private static string Controller(string ns, string route) => $$"""
        using Microsoft.AspNetCore.Mvc;
        namespace {{ns}};
        [ApiController, Route("{{route}}")]
        public class ThingsController : ControllerBase
        {
            [HttpGet("{id}")] public IActionResult Get(int id) => null!;
        }
        """;

    // ---- finding 1: unreadable directories ----

    /// <summary>
    /// A dangling directory link (junction on Windows, symlink elsewhere) is listed as a directory but throws
    /// DirectoryNotFoundException when read: a deterministic, privilege-free stand-in for an access-denied folder.
    /// (A deny-ACL is not reliable: the .NET 9 test host on this machine still enumerates a folder whose
    /// ListDirectory right is denied.)
    /// </summary>
    private static void DanglingDirectoryLink(string link)
    {
        var target = link + "-target";
        Directory.CreateDirectory(target);
        if (OperatingSystem.IsWindows())
        {
            using var p = Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"")
                { UseShellExecute = false, RedirectStandardOutput = true, CreateNoWindow = true })!;
            p.WaitForExit();
            Assert.Equal(0, p.ExitCode);
        }
        else
        {
            Directory.CreateSymbolicLink(link, target);
        }
        Directory.Delete(target);
        Assert.Throws<DirectoryNotFoundException>(() => Directory.GetFiles(link));
    }

    [Fact]
    public void UnreadableDirectory_BecomesParseError_RestOfRepoStillScanned()
    {
        using var ws = new TempWorkspace()
            .File("r/src/Good.Api/Good.Api.csproj", WebCsproj)
            .File("r/src/Good.Api/C.cs", Controller("Good", "api/good"))
            .Repo("r");
        DanglingDirectoryLink(Path.Combine(ws.Root, "r", "src", "Broken"));
        DanglingDirectoryLink(Path.Combine(ws.Root, "r", "src", "Good.Api", "Generated"));

        var g = new ScanOrchestrator().Scan(ws.Root);

        Assert.Equal(["proj:r/Good.Api"], g.Projects.Select(p => p.Id));
        Assert.Equal(["ep:r:GET:/api/good/{id}"], g.Endpoints.Select(e => e.Id));
        var errors = g.Diagnostics.Where(d => d.Kind == DiagnosticKinds.ParseError).ToList();
        Assert.Equal(["r/src/Broken/", "r/src/Good.Api/Generated/"], errors.Select(d => d.Message.Split(':')[0]));
        Assert.All(errors, d => Assert.Equal((Severities.Warning, "repo:r"), (d.Severity, d.NodeIds.Single())));
        Assert.All(errors, d => Assert.Contains(": directory skipped:", d.Message));
        Assert.All(errors, d => Assert.DoesNotContain(ws.Root, d.Message, StringComparison.OrdinalIgnoreCase));
        GraphIntegrity.AssertValid(g);

        var manifest = WorkspaceManifest.Compute(ws.Root); // must not throw either
        Assert.Equal("unreadable", manifest["r/src/Broken/"]);
    }

    // ---- finding 2: duplicate / repeated / nested repo paths ----

    [Fact]
    public void SameNamedRepos_GetUniqueNames_AndConsistentIds()
    {
        using var ws = new TempWorkspace()
            .File("depenk.yml", "repos:\n  paths: [a/common, b/common, a/common, ./a/common/]\n")
            .File("a/common/src/Common.Api/Common.Api.csproj", WebCsproj)
            .File("a/common/src/Common.Api/C.cs", Controller("A", "api/things"))
            .File("a/common/src/Common.Client/Common.Client.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><IsPackable>true</IsPackable></PropertyGroup></Project>")
            .File("a/common/src/Common.Client/ThingsClient.cs",
                "namespace A; public class ThingsClient(HttpClient http) { public Task<string> Get(int id) => http.GetStringAsync($\"api/things/{id}\"); }")
            .File("b/common/src/Common.Api/Common.Api.csproj", WebCsproj)
            .File("b/common/src/Common.Api/C.cs", Controller("B", "api/things"))
            .File("b/common/src/Common.Client/Common.Client.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><IsPackable>true</IsPackable></PropertyGroup></Project>")
            .File("b/common/src/Common.Client/ThingsClient.cs",
                "namespace B; public class ThingsClient(HttpClient http) { public Task<string> Get(int id) => http.GetStringAsync($\"api/things/{id}\"); }")
            .Repo("a/common").Repo("b/common");

        var g = new ScanOrchestrator().Scan(ws.Root);

        Assert.Equal([("repo:common", "a/common"), ("repo:common#2", "b/common")], g.Repos.Select(r => (r.Id, r.Path)));
        Assert.Equal(["proj:common#2/Common.Api", "proj:common#2/Common.Client", "proj:common/Common.Api", "proj:common/Common.Client"],
            g.Projects.Select(p => p.Id));
        Assert.Equal(["ep:common#2:GET:/api/things/{id}", "ep:common:GET:/api/things/{id}"], g.Endpoints.Select(e => e.Id));
        Assert.All(g.Endpoints, e => Assert.StartsWith($"proj:{e.Repo}/", e.ProjectId));
        // each repo's client targets its own repo's endpoint
        Assert.Contains(g.EdgesOf(EdgeKind.Targets), e => e.To == "ep:common:GET:/api/things/{id}"
            && g.ClientMethods.Single(c => c.Id == e.From).Repo == "common");
        Assert.Contains(g.EdgesOf(EdgeKind.Targets), e => e.To == "ep:common#2:GET:/api/things/{id}"
            && g.ClientMethods.Single(c => c.Id == e.From).Repo == "common#2");

        var d = Assert.Single(g.Diagnostics, d => d.Kind == DiagnosticKinds.DuplicateRepoName);
        Assert.Equal(Severities.Info, d.Severity);
        Assert.Equal(["repo:common", "repo:common#2"], d.NodeIds);
        Assert.Contains("a/common", d.Message);
        Assert.Contains("b/common", d.Message);
        GraphIntegrity.AssertValid(g);
    }

    [Fact]
    public void NestedRepoPaths_InnermostRepoOwnsItsProjects()
    {
        using var ws = new TempWorkspace()
            .File("depenk.yml", "repos:\n  paths: [outer, outer/inner]\n")
            .File("outer/Outer.Api/Outer.Api.csproj", WebCsproj)
            .File("outer/Outer.Api/C.cs", Controller("O", "api/outer"))
            .File("outer/inner/Inner.Api/Inner.Api.csproj", WebCsproj)
            .File("outer/inner/Inner.Api/C.cs", Controller("I", "api/inner"))
            .Repo("outer").Repo("outer/inner");

        var g = new ScanOrchestrator().Scan(ws.Root);

        Assert.Equal(["proj:inner/Inner.Api", "proj:outer/Outer.Api"], g.Projects.Select(p => p.Id));
        Assert.Equal(["ep:inner:GET:/api/inner/{id}", "ep:outer:GET:/api/outer/{id}"], g.Endpoints.Select(e => e.Id));
        GraphIntegrity.AssertValid(g);
    }

    // ---- finding 3: model ids across same-named projects ----

    [Fact]
    public void SameNamedProjectsInDifferentRepos_GetDistinctModelIds_EdgesPointAtOwnModel()
    {
        static string Ctl(string route) => $$"""
            using Microsoft.AspNetCore.Mvc;
            namespace Api.Controllers;
            [ApiController, Route("{{route}}")]
            public class ThingsController : ControllerBase
            {
                [HttpGet] public Api.Models.ErrorDto Get() => null!;
            }
            """;
        using var ws = new TempWorkspace()
            .File("orders/Api/Api.csproj", WebCsproj)
            .File("orders/Api/C.cs", Ctl("api/orders"))
            .File("orders/Api/E.cs", "namespace Api.Models; public class ErrorDto { public string OrderCode { get; set; } = \"\"; }")
            .File("billing/Api/Api.csproj", WebCsproj)
            .File("billing/Api/C.cs", Ctl("api/billing"))
            .File("billing/Api/E.cs", "namespace Api.Models; public class ErrorDto { public int BillingCode { get; set; } }")
            .Repo("orders").Repo("billing");

        var g = new ScanOrchestrator().Scan(ws.Root);

        var dtos = g.Models.Where(m => m.FullName == "Api.Models.ErrorDto").ToList();
        Assert.Equal(["model:Api:Api.Models.ErrorDto", "model:Api:Api.Models.ErrorDto#2"], dtos.Select(m => m.Id));
        foreach (var repo in new[] { "orders", "billing" })
        {
            var ret = g.EdgesOf(EdgeKind.Returns).Single(e => e.From == $"ep:{repo}:GET:/api/{repo}");
            var model = g.Models.Single(m => m.Id == ret.To);
            Assert.Equal((repo, $"proj:{repo}/Api"), (model.Repo, model.ProjectId));
            Assert.Equal(Confidence.High, ret.Confidence);
        }
        Assert.DoesNotContain(g.Diagnostics, d => d.Kind == DiagnosticKinds.AmbiguousModel);
        GraphIntegrity.AssertValid(g);
    }

    // ---- finding 8: per-project isolation ----

    private sealed class ThrowingFinder(string projectName) : IEndpointFinder
    {
        public IEnumerable<EndpointNode> Find(SourceSet src) =>
            src.ProjectName == projectName ? throw new InvalidOperationException("boom in " + src.ProjectName) : [];
    }

    private static TempWorkspace TwoApis() => new TempWorkspace()
        .File("r/src/Good.Api/Good.Api.csproj", WebCsproj)
        .File("r/src/Good.Api/C.cs", Controller("Good", "api/good"))
        .File("r/src/Bad.Api/Bad.Api.csproj", WebCsproj)
        .File("r/src/Bad.Api/C.cs", Controller("Bad", "api/bad"))
        .Repo("r");

    [Fact]
    public void ThrowingAnalyzer_BecomesParseError_ScanContinues()
    {
        using var ws = TwoApis();
        var orchestrator = new ScanOrchestrator(null, [new ControllerEndpointFinder(), new ThrowingFinder("Bad.Api")]);

        var g = orchestrator.Scan(ws.Root);

        Assert.Equal(["ep:r:GET:/api/good/{id}"], g.Endpoints.Select(e => e.Id));
        Assert.Equal(["proj:r/Bad.Api", "proj:r/Good.Api"], g.Projects.Select(p => p.Id));
        var d = Assert.Single(g.Diagnostics, d => d.Kind == DiagnosticKinds.ParseError);
        Assert.Equal((Severities.Warning, "repo:r"), (d.Severity, d.NodeIds.Single()));
        Assert.Equal("r/src/Bad.Api/Bad.Api.csproj: analysis failed: boom in Bad.Api", d.Message);
        GraphIntegrity.AssertValid(g);

        // a failed analysis is not cached: the next scan retries it
        var before = orchestrator.AnalyzedProjectCount;
        orchestrator.Scan(ws.Root);
        Assert.Equal(before + 1, orchestrator.AnalyzedProjectCount);
    }

    [Fact]
    public void ThrowingCallSiteScan_BecomesParseError_ScanContinues()
    {
        using var ws = TwoApis();
        var g = new ScanOrchestrator { FaultInjection = (stage, id) =>
        {
            if (stage == "callSites" && id == "proj:r/Bad.Api") throw new IOException($"disk gone under {ws.Root}");
        } }.Scan(ws.Root);

        Assert.Equal(2, g.Endpoints.Count);
        var d = Assert.Single(g.Diagnostics, d => d.Kind == DiagnosticKinds.ParseError);
        Assert.StartsWith("r/src/Bad.Api/Bad.Api.csproj: analysis failed: disk gone under", d.Message);
        Assert.DoesNotContain(ws.Root, d.Message, StringComparison.OrdinalIgnoreCase);
        GraphIntegrity.AssertValid(g);
    }
}
