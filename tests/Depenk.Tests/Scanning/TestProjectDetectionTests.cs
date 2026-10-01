using Depenk.Analysis;
using Depenk.Core.Model;
using Depenk.Scanning;
using Depenk.Tests.TestUtil;

namespace Depenk.Tests.Scanning;

public class TestProjectDetectionTests
{
    private const string Controller = """
        using Microsoft.AspNetCore.Mvc;
        namespace Acme;
        [ApiController, Route("api/orders")]
        public class OrdersController : ControllerBase
        {
            [HttpGet("{id}")] public IActionResult Get(int id) => null!;
        }
        """;

    private static string P(TempWorkspace ws, string rel) => Path.Combine(ws.Root, rel);

    private static TempWorkspace RepoWithProps(string props) => new TempWorkspace()
        .File("r/Directory.Build.props", props)
        .File("r/src/Orders.Api/Orders.Api.csproj", "<Project Sdk=\"Microsoft.NET.Sdk.Web\" />")
        .File("r/src/Orders.Api/OrdersController.cs", Controller)
        .File("r/tests/Orders.Api.Tests/Orders.Api.Tests.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\" />")
        .Repo("r");

    private static void AssertApiNotTestAndTestsAreTest(TempWorkspace ws)
    {
        var g = new ScanOrchestrator().Scan(ws.Root);
        Assert.Equal(ProjectKind.Api, g.Projects.Single(p => p.Name == "Orders.Api").Kind);
        Assert.Equal(ProjectKind.Test, g.Projects.Single(p => p.Name == "Orders.Api.Tests").Kind);
        Assert.Single(g.Endpoints, e => e.Id == "ep:r:GET:/api/orders/{id}");
        GraphIntegrity.AssertValid(g);
    }

    [Fact]
    public void ConditionalItemGroup_InRootProps_DoesNotMarkEveryProjectTest()
    {
        using var ws = RepoWithProps("""
            <Project>
              <ItemGroup Condition="'$(IsTestProject)' == 'true'">
                <PackageReference Include="Microsoft.NET.Test.Sdk" />
              </ItemGroup>
            </Project>
            """);
        AssertApiNotTestAndTestsAreTest(ws);
    }

    [Fact]
    public void ConditionOnPackageReferenceItself_InRootProps_IsIgnored()
    {
        using var ws = RepoWithProps("""
            <Project>
              <ItemGroup>
                <PackageReference Include="Microsoft.NET.Test.Sdk" Condition="$(MSBuildProjectName.EndsWith('Tests'))" />
              </ItemGroup>
            </Project>
            """);
        AssertApiNotTestAndTestsAreTest(ws);
    }

    private const string UnconditionalProps = """
        <Project><ItemGroup><PackageReference Include="Microsoft.NET.Test.Sdk" /></ItemGroup></Project>
        """;

    [Fact]
    public void UnconditionalPropsTestSdk_MarksTestProject()
    {
        using var ws = new TempWorkspace().File("r/Directory.Build.props", UnconditionalProps)
            .File("r/Foo/Foo.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        Assert.True(ProjectParser.Parse(P(ws, "r/Foo/Foo.csproj"), P(ws, "r")).IsTestProject);
    }

    [Fact]
    public void UnconditionalPropsTestSdk_ExplicitIsTestProjectFalse_Wins()
    {
        using var ws = new TempWorkspace().File("r/Directory.Build.props", UnconditionalProps)
            .File("r/Foo/Foo.csproj",
                "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><IsTestProject>false</IsTestProject></PropertyGroup></Project>");
        Assert.False(ProjectParser.Parse(P(ws, "r/Foo/Foo.csproj"), P(ws, "r")).IsTestProject);
    }

    [Fact]
    public void NameFallback_ExplicitIsTestProjectFalse_Wins()
    {
        using var ws = new TempWorkspace().File("r/Foo.Tests/Foo.Tests.csproj",
            "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><IsTestProject>false</IsTestProject></PropertyGroup></Project>");
        Assert.False(ProjectParser.Parse(P(ws, "r/Foo.Tests/Foo.Tests.csproj"), P(ws, "r")).IsTestProject);
    }

    [Fact]
    public void ExplicitIsTestProjectTrue_InProject_StillMarksTest()
    {
        using var ws = new TempWorkspace().File("r/Foo/Foo.csproj",
            "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><IsTestProject>true</IsTestProject></PropertyGroup></Project>");
        Assert.True(ProjectParser.Parse(P(ws, "r/Foo/Foo.csproj"), P(ws, "r")).IsTestProject);
    }
}
