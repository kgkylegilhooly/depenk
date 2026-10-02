using Depenk.Scanning;
using Depenk.Tests.TestUtil;

namespace Depenk.Tests.Scanning;

public class WorkspaceResolverTests
{
    private static string? NoEnv(string _) => null;

    [Fact]
    public void ExplicitPath_Wins()
    {
        using var ws = new TempWorkspace().Repo("a");
        Assert.Equal(Path.Combine(ws.Root, "a"),
            WorkspaceResolver.Resolve(Path.Combine(ws.Root, "a"), ws.Root, _ => "/elsewhere"));
    }

    [Fact]
    public void EnvVar_WinsOverCurrentDirectory()
    {
        using var ws = new TempWorkspace().Repo("a");
        Assert.Equal(ws.Root, WorkspaceResolver.Resolve(null, Path.Combine(ws.Root, "a"),
            k => k == WorkspaceResolver.EnvVar ? ws.Root : null));
    }

    [Fact]
    public void RepoWithSiblingRepos_ResolvesToParent()
    {
        using var ws = new TempWorkspace().Repo("orders").Repo("billing");
        Assert.Equal(ws.Root, WorkspaceResolver.Resolve(null, Path.Combine(ws.Root, "orders"), NoEnv));
    }

    [Fact]
    public void LoneRepo_StaysPut_UnlessParentIsMarked()
    {
        using var ws = new TempWorkspace().Repo("orders").File("notes/readme.md", "x");
        var orders = Path.Combine(ws.Root, "orders");
        Assert.Equal(orders, WorkspaceResolver.Resolve(null, orders, NoEnv));

        ws.File("depenk.yml", "repos: {}\n");
        Assert.Equal(ws.Root, WorkspaceResolver.Resolve(null, orders, NoEnv));
    }

    [Fact]
    public void MarkedCurrentDirectory_StaysPut()
    {
        using var ws = new TempWorkspace().Repo("orders").Repo("billing").File("orders/depenk.yml", "repos: {}\n");
        var orders = Path.Combine(ws.Root, "orders");
        Assert.Equal(orders, WorkspaceResolver.Resolve(null, orders, NoEnv));
    }
}
