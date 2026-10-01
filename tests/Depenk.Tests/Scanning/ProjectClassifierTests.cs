using Depenk.Core.Model;
using Depenk.Scanning;
using Depenk.Scanning.Config;

namespace Depenk.Tests.Scanning;

public class ProjectClassifierTests
{
    private static ProjectFile Pf(string name, string? sdk = "Microsoft.NET.Sdk", bool packable = false, bool test = false) =>
        new($"/x/{name}.csproj", name, sdk, name, false, null, packable, test, [], []);

    [Theory]
    [InlineData("Orders.Api", "Microsoft.NET.Sdk.Web", false, false, false, false, ProjectKind.Api)]
    [InlineData("Orders.Functions", "Microsoft.NET.Sdk", false, false, true, false, ProjectKind.Api)]
    [InlineData("Orders.Client", "Microsoft.NET.Sdk", false, false, false, true, ProjectKind.Client)]
    [InlineData("Orders.Sdk2", "Microsoft.NET.Sdk", true, false, false, true, ProjectKind.Client)]
    [InlineData("Orders.Client", "Microsoft.NET.Sdk", false, false, false, false, ProjectKind.Other)]
    [InlineData("Shared.Kernel", "Microsoft.NET.Sdk", true, false, false, false, ProjectKind.Library)]
    [InlineData("Orders.Tests", "Microsoft.NET.Sdk", false, true, true, true, ProjectKind.Test)]
    public void Classifies(string name, string sdk, bool packable, bool test, bool hasEndpoints, bool hasHttp, ProjectKind expected)
    {
        var kind = ProjectClassifier.Classify(Pf(name, sdk, packable, test), new ProjectSignals(hasEndpoints, hasHttp), new DepenkConfig());
        Assert.Equal(expected, kind);
    }

    [Fact]
    public void ConfigOverrideWins_CaseInsensitive()
    {
        var cfg = new DepenkConfig();
        cfg.Projects.KindOverrides["Orders.Contracts"] = "client";
        Assert.Equal(ProjectKind.Client,
            ProjectClassifier.Classify(Pf("Orders.Contracts", packable: true), new ProjectSignals(false, false), cfg));
    }
}
