using Depenk.Scanning.Config;
using Depenk.Tests.TestUtil;

namespace Depenk.Tests.Scanning;

public class ConfigLoaderTests
{
    [Fact]
    public void MissingFile_ReturnsDefaults()
    {
        using var ws = new TempWorkspace();
        var cfg = ConfigLoader.Load(ws.Root);
        Assert.Equal(["*"], cfg.Repos.Include);
        Assert.Empty(cfg.HttpWrappers);
    }

    [Fact]
    public void ParsesAllSections()
    {
        using var ws = new TempWorkspace().File("depenk.yml", """
            repos:
              include: ["*"]
              exclude: ["legacy-*"]
            projects:
              kindOverrides:
                Orders.Contracts: Client
              ignore: ["*.Benchmarks"]
            packages:
              producers:
                Acme.Orders.Client: orders
            httpWrappers:
              - type: "*.IApiHttpClient"
                methods: { "Get*": GET, "Post*": POST }
                routeArgument: 0
            routes:
              prefixes:
                Orders.Client: /api
            """);
        var cfg = ConfigLoader.Load(ws.Root);
        Assert.Equal(["legacy-*"], cfg.Repos.Exclude);
        Assert.Equal("Client", cfg.Projects.KindOverrides["Orders.Contracts"]);
        Assert.Equal("orders", cfg.Packages.Producers["Acme.Orders.Client"]);
        Assert.Equal("GET", cfg.HttpWrappers[0].Methods["Get*"]);
        Assert.Equal("/api", cfg.Routes.Prefixes["Orders.Client"]);
    }

    [Fact]
    public void InvalidYaml_ThrowsWithLineNumber()
    {
        using var ws = new TempWorkspace().File("depenk.yml", "repos:\n  include: [\"*\"\n  exclude: x\n");
        var ex = Assert.Throws<ConfigException>(() => ConfigLoader.Load(ws.Root));
        Assert.Matches(@"depenk\.yml\(\d+\)", ex.Message);
    }

    [Theory]
    [InlineData("*.IApiHttpClient", "Acme.Http.IApiHttpClient", true)]
    [InlineData("legacy-*", "legacy-billing", true)]
    [InlineData("legacy-*", "billing", false)]
    [InlineData("Get*", "getasync", true)]
    [InlineData("Order?", "Orders", true)]
    public void Glob_Matches(string pattern, string value, bool expected) =>
        Assert.Equal(expected, Glob.IsMatch(pattern, value));
}
