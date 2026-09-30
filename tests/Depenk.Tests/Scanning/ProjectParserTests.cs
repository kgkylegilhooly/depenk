using Depenk.Scanning;
using Depenk.Tests.TestUtil;

namespace Depenk.Tests.Scanning;

public class ProjectParserTests
{
    private static string P(TempWorkspace ws, string rel) => Path.Combine(ws.Root, rel);

    [Fact]
    public void ParsesSdk_PackageId_Version_AndReferences()
    {
        using var ws = new TempWorkspace().File("orders/src/Orders.Client/Orders.Client.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <PackageId>Acme.Orders.Client</PackageId>
                <Version>3.4.1</Version>
                <IsPackable>true</IsPackable>
              </PropertyGroup>
              <ItemGroup>
                <PackageReference Include="Refit" Version="7.2.1" />
                <PackageReference Include="Acme.Http"><Version>1.0.0</Version></PackageReference>
                <ProjectReference Include="..\Orders.Models\Orders.Models.csproj" />
              </ItemGroup>
            </Project>
            """);
        var pf = ProjectParser.Parse(P(ws, "orders/src/Orders.Client/Orders.Client.csproj"), P(ws, "orders"));

        Assert.Equal("Orders.Client", pf.Name);
        Assert.Equal("Microsoft.NET.Sdk", pf.Sdk);
        Assert.Equal("Acme.Orders.Client", pf.EffectivePackageId);
        Assert.True(pf.ExplicitPackageId);
        Assert.Equal("3.4.1", pf.Version);
        Assert.True(pf.IsPackable);
        Assert.Equal([new PackageRef("Refit", "7.2.1"), new PackageRef("Acme.Http", "1.0.0")], pf.PackageReferences);
        Assert.EndsWith(Path.Combine("Orders.Models", "Orders.Models.csproj"), pf.ProjectReferences.Single());
    }

    [Fact]
    public void ResolvesProperties_FromDirectoryBuildProps_AndCentralVersions()
    {
        using var ws = new TempWorkspace()
            .File("billing/Directory.Build.props", """
                <Project><PropertyGroup><AcmeVersion>2.0.0</AcmeVersion><Version>9.9.9</Version></PropertyGroup></Project>
                """)
            .File("billing/Directory.Packages.props", """
                <Project><ItemGroup>
                  <PackageVersion Include="Acme.Orders.Client" Version="3.2.0" />
                  <PackageVersion Include="Acme.Http" Version="$(AcmeVersion)" />
                </ItemGroup></Project>
                """)
            .File("billing/src/Billing.Api/Billing.Api.csproj", """
                <Project Sdk="Microsoft.NET.Sdk.Web">
                  <PropertyGroup><Version>1.2.3</Version></PropertyGroup>
                  <ItemGroup>
                    <PackageReference Include="Acme.Orders.Client" />
                    <PackageReference Include="Acme.Http" />
                    <PackageReference Include="Serilog" VersionOverride="4.0.0" />
                    <PackageReference Include="Missing.Pkg" Version="$(NotDefinedAnywhere)" />
                  </ItemGroup>
                </Project>
                """);
        var pf = ProjectParser.Parse(P(ws, "billing/src/Billing.Api/Billing.Api.csproj"), P(ws, "billing"));

        Assert.Equal("1.2.3", pf.Version); // project overrides props
        Assert.Equal("3.2.0", pf.PackageReferences.Single(r => r.Id == "Acme.Orders.Client").Version);
        Assert.Equal("2.0.0", pf.PackageReferences.Single(r => r.Id == "Acme.Http").Version);
        Assert.Equal("4.0.0", pf.PackageReferences.Single(r => r.Id == "Serilog").Version);
        Assert.Equal("unresolved($(NotDefinedAnywhere))", pf.PackageReferences.Single(r => r.Id == "Missing.Pkg").Version);
        Assert.True(ProjectFile.IsUnresolved(pf.PackageReferences.Single(r => r.Id == "Missing.Pkg").Version));
    }

    [Fact]
    public void DefaultsPackageId_ToAssemblyName_ThenFileName_AndDetectsTests()
    {
        using var ws = new TempWorkspace()
            .File("r/A/A.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><AssemblyName>Acme.A</AssemblyName></PropertyGroup></Project>")
            .File("r/B/B.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\"></Project>")
            .File("r/T/T.csproj", """
                <Project Sdk="Microsoft.NET.Sdk"><ItemGroup><PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.0.0" /></ItemGroup></Project>
                """);
        Assert.Equal("Acme.A", ProjectParser.Parse(P(ws, "r/A/A.csproj"), P(ws, "r")).EffectivePackageId);
        var b = ProjectParser.Parse(P(ws, "r/B/B.csproj"), P(ws, "r"));
        Assert.Equal("B", b.EffectivePackageId);
        Assert.False(b.ExplicitPackageId);
        Assert.True(ProjectParser.Parse(P(ws, "r/T/T.csproj"), P(ws, "r")).IsTestProject);
    }

    [Fact]
    public void HandlesLegacyMsbuildNamespace()
    {
        using var ws = new TempWorkspace().File("r/L/L.csproj", """
            <Project ToolsVersion="15.0" xmlns="http://schemas.microsoft.com/developer/msbuild/2003">
              <ItemGroup><PackageReference Include="Newtonsoft.Json" Version="13.0.3" /></ItemGroup>
            </Project>
            """);
        var pf = ProjectParser.Parse(P(ws, "r/L/L.csproj"), P(ws, "r"));
        Assert.Null(pf.Sdk);
        Assert.Equal("Newtonsoft.Json", pf.PackageReferences.Single().Id);
    }

    [Fact]
    public void MalformedXml_ThrowsProjectParseException_WithPath()
    {
        using var ws = new TempWorkspace().File("r/Bad/Bad.csproj", "<Project><PropertyGroup></Project>");
        var ex = Assert.Throws<ProjectParseException>(() => ProjectParser.Parse(P(ws, "r/Bad/Bad.csproj"), P(ws, "r")));
        Assert.Contains("Bad.csproj", ex.Message);
    }
}
