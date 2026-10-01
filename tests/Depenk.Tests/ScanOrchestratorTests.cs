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
}
