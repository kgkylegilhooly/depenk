using Depenk.Scanning;
using Depenk.Scanning.Config;
using Depenk.Tests.TestUtil;

namespace Depenk.Tests.Scanning;

public class RepoDiscoveryTests
{
    [Fact]
    public void FindsChildGitRepos_AppliesFilters_ReadsHead()
    {
        using var ws = new TempWorkspace().Repo("orders", "aaaa000000000000000000000000000000000000")
            .Repo("billing").Repo("legacy-crm").File("notarepo/readme.md", "x");
        var cfg = new DepenkConfig { Repos = new RepoFilter { Exclude = ["legacy-*"] } };

        var repos = RepoDiscovery.Discover(ws.Root, cfg);

        Assert.Equal(["billing", "orders"], repos.Select(r => r.Name));
        var orders = repos.Single(r => r.Name == "orders");
        Assert.Equal("aaaa000000000000000000000000000000000000", orders.HeadSha);
        Assert.Equal("orders", orders.RelativePath);
    }

    [Fact]
    public void ReadsPackedRefs_WhenLooseRefMissing()
    {
        using var ws = new TempWorkspace()
            .File("orders/.git/HEAD", "ref: refs/heads/main\n")
            .File("orders/.git/packed-refs", "# pack-refs\nbbbb000000000000000000000000000000000000 refs/heads/main\n");
        var repo = RepoDiscovery.Discover(ws.Root, new DepenkConfig()).Single();
        Assert.Equal("bbbb000000000000000000000000000000000000", repo.HeadSha);
    }

    [Fact]
    public void ExplicitPaths_ReplaceDiscovery_AndMayBeNestedOrNonGit()
    {
        using var ws = new TempWorkspace().Repo("orders").File("group/billing/readme.md", "x");
        var cfg = new DepenkConfig { Repos = new RepoFilter { Paths = ["group/billing", "missing"] } };

        var repo = RepoDiscovery.Discover(ws.Root, cfg).Single();

        Assert.Equal(("billing", "group/billing", (string?)null), (repo.Name, repo.RelativePath, repo.HeadSha));
    }

    [Fact]
    public void WorkspaceItselfIsRepo_WhenNoChildRepos()
    {
        using var ws = new TempWorkspace().File(".git/HEAD", "0123456789abcdef0123456789abcdef01234567\n");
        var repo = RepoDiscovery.Discover(ws.Root, new DepenkConfig()).Single();
        Assert.Equal(".", repo.RelativePath);
        Assert.Equal(Path.GetFileName(ws.Root), repo.Name);
    }

    [Fact]
    public void RelativePaths_UseForwardSlashes()
    {
        Assert.Equal("orders/src/A.csproj",
            PathUtil.Rel(@"C:\ws", Path.Combine(@"C:\ws", "orders", "src", "A.csproj")));
    }
}
