using Depenk.Analysis;
using Depenk.Core.Model;
using Depenk.Tests.TestUtil;

namespace Depenk.Tests;

public class ScanOrchestratorTests
{
    private static TempWorkspace SharedClientWorkspace()
    {
        static string Csproj() => """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><PackageId>Shared.Client</PackageId><Version>1.0.0</Version><IsPackable>true</IsPackable></PropertyGroup>
            </Project>
            """;
        static string Client(string route) => $$"""
            namespace Acme.Things;
            public class ThingsClient(HttpClient http)
            {
                public Task<string> GetThing(int id) => http.GetStringAsync($"{{route}}/{id}");
            }
            """;
        return new TempWorkspace()
            .File("repoa/src/Shared.Client/Shared.Client.csproj", Csproj())
            .File("repoa/src/Shared.Client/ThingsClient.cs", Client("api/a-things"))
            .File("repob/src/Shared.Client/Shared.Client.csproj", Csproj())
            .File("repob/src/Shared.Client/ThingsClient.cs", Client("api/b-things"))
            .File("consumer/src/Consumer.Api/Consumer.Api.csproj", """
                <Project Sdk="Microsoft.NET.Sdk.Web">
                  <ItemGroup><PackageReference Include="Shared.Client" Version="1.0.0" /></ItemGroup>
                </Project>
                """)
            .File("consumer/src/Consumer.Api/Uses.cs", """
                using Acme.Things;
                namespace Acme.Consumer;
                public class Uses(ThingsClient things)
                {
                    public Task<string> Run() => things.GetThing(1);
                }
                """)
            .Repo("repoa").Repo("repob").Repo("consumer");
    }

    [Fact]
    public void SameNamedClientProjects_AcrossRepos_DoNotCrashAndGetDistinctIds()
    {
        using var ws = SharedClientWorkspace();
        var g = new ScanOrchestrator().Scan(ws.Root);

        Assert.Equal(2, g.ClientMethods.Count);
        Assert.Equal(2, g.ClientMethods.Select(c => c.Id).Distinct().Count());
        Assert.Contains(g.ClientMethods, c => c.Id == "cm:Shared.Client:ThingsClient.GetThing");
        Assert.Contains(g.ClientMethods, c => c.Id == "cm:Shared.Client:ThingsClient.GetThing#2");
        Assert.Contains(g.Diagnostics, d => d.Kind == DiagnosticKinds.AmbiguousProducer);
        Assert.Equal(g.CallSites.Count, g.CallSites.Select(c => c.Id).Distinct().Count());
        var invokes = g.EdgesOf(EdgeKind.Invokes).ToList();
        Assert.All(invokes, e => Assert.Contains(g.CallSites, c => c.Id == e.From));
        Assert.All(invokes, e => Assert.Contains(g.ClientMethods, c => c.Id == e.To));
    }

    [Fact]
    public void SameNamedClientProjects_InOneRepo_DoNotCrash()
    {
        using var ws = new TempWorkspace()
            .File("r/src/a/Shared.Client.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><PackageId>Shared.Client</PackageId><IsPackable>true</IsPackable></PropertyGroup></Project>")
            .File("r/src/a/ThingsClient.cs", "namespace A; public class ThingsClient(HttpClient http) { public Task<string> GetThing(int id) => http.GetStringAsync($\"api/a/{id}\"); }")
            .File("r/src/b/Shared.Client.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><PackageId>Shared.Client</PackageId><IsPackable>true</IsPackable></PropertyGroup></Project>")
            .File("r/src/b/ThingsClient.cs", "namespace B; public class ThingsClient(HttpClient http) { public Task<string> GetThing(int id) => http.GetStringAsync($\"api/b/{id}\"); }")
            .Repo("r");
        var g = new ScanOrchestrator().Scan(ws.Root);
        Assert.Equal(g.ClientMethods.Count, g.ClientMethods.Select(c => c.Id).Distinct().Count());
        Assert.Contains(g.Diagnostics, d => d.Kind == DiagnosticKinds.DuplicateProjectName);
    }

    [Fact]
    public void IdenticalControllerRoutes_InOneRepo_GetNumberedEndpointIds()
    {
        const string Controller = """
            using Microsoft.AspNetCore.Mvc;
            namespace Acme;
            [ApiController, Route("api/things")]
            public class ThingsController : ControllerBase
            {
                [HttpGet("{id}")] public IActionResult Get(int id) => null!;
            }
            """;
        using var ws = new TempWorkspace()
            .File("r/src/Api1/Api1.csproj", "<Project Sdk=\"Microsoft.NET.Sdk.Web\" />")
            .File("r/src/Api1/C.cs", Controller)
            .File("r/src/Api2/Api2.csproj", "<Project Sdk=\"Microsoft.NET.Sdk.Web\" />")
            .File("r/src/Api2/C.cs", Controller)
            .Repo("r");
        var g = new ScanOrchestrator().Scan(ws.Root);
        Assert.Equal(["ep:r:GET:/api/things/{id}", "ep:r:GET:/api/things/{id}#2"], g.Endpoints.Select(e => e.Id));
    }

    [Fact]
    public void ClientOverloads_TargetingDifferentRoutes_AreAllLinked()
    {
        using var ws = new TempWorkspace()
            .File("svc/src/Svc.Api/Svc.Api.csproj", "<Project Sdk=\"Microsoft.NET.Sdk.Web\" />")
            .File("svc/src/Svc.Api/C.cs", """
                using Microsoft.AspNetCore.Mvc;
                namespace Svc;
                [ApiController, Route("api")]
                public class ThingsController : ControllerBase
                {
                    [HttpGet("a")] public string A() => "";
                    [HttpGet("b")] public string B(int p) => "";
                }
                """)
            .File("svc/src/Svc.Client/Svc.Client.csproj",
                "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><PackageId>Svc.Client</PackageId><IsPackable>true</IsPackable></PropertyGroup></Project>")
            .File("svc/src/Svc.Client/ThingsClient.cs", """
                namespace Svc.Client;
                public class ThingsClient(HttpClient http)
                {
                    public Task<string> ListAsync() => http.GetStringAsync("api/a");
                    public Task<string> ListAsync(int p) => http.GetStringAsync($"api/b?p={p}");
                }
                """)
            .File("app/src/App/App.csproj", """
                <Project Sdk="Microsoft.NET.Sdk"><ItemGroup><PackageReference Include="Svc.Client" Version="1.0.0" /></ItemGroup></Project>
                """)
            .File("app/src/App/Uses.cs", """
                using Svc.Client;
                namespace App;
                public class Uses(ThingsClient things)
                {
                    public Task<string> Run() => things.ListAsync(2);
                }
                """)
            .Repo("svc").Repo("app");

        var g = new ScanOrchestrator().Scan(ws.Root);

        var targets = g.EdgesOf(EdgeKind.Targets).Select(e => (e.From, e.To)).ToList();
        Assert.Equal([
            ("cm:Svc.Client:ThingsClient.ListAsync", "ep:svc:GET:/api/a"),
            ("cm:Svc.Client:ThingsClient.ListAsync#2", "ep:svc:GET:/api/b"),
        ], targets);
        Assert.DoesNotContain(g.Diagnostics, d => d.Kind == DiagnosticKinds.UnusedEndpoint);
        // the one-argument call resolves to the one-parameter overload only
        var invoke = Assert.Single(g.EdgesOf(EdgeKind.Invokes));
        Assert.Equal(("cm:Svc.Client:ThingsClient.ListAsync#2", Confidence.Medium), (invoke.To, invoke.Confidence));
        Assert.DoesNotContain(g.Diagnostics, d => d.Kind == DiagnosticKinds.AmbiguousCallSite);
        GraphIntegrity.AssertValid(g);
    }
}
