using Depenk.Core.Model;

namespace Depenk.Tests.TestUtil;

/// <summary>Structural invariants every scanned graph must satisfy.</summary>
public static class GraphIntegrity
{
    public static IEnumerable<string> NodeIds(DepGraph g) =>
        g.Repos.Select(n => n.Id)
            .Concat(g.Projects.Select(n => n.Id))
            .Concat(g.Packages.Select(n => n.Id))
            .Concat(g.Endpoints.Select(n => n.Id))
            .Concat(g.ClientMethods.Select(n => n.Id))
            .Concat(g.CallSites.Select(n => n.Id))
            .Concat(g.Models.Select(n => n.Id));

    /// <summary>Node ids are unique across all node lists; every edge end and diagnostic node id resolves to a node.</summary>
    public static void AssertValid(DepGraph g)
    {
        var ids = NodeIds(g).ToList();
        var duplicates = ids.GroupBy(i => i, StringComparer.Ordinal).Where(x => x.Count() > 1).Select(x => x.Key).ToList();
        Assert.True(duplicates.Count == 0, "Duplicate node ids: " + string.Join(", ", duplicates));

        var known = ids.ToHashSet(StringComparer.Ordinal);
        var dangling = g.Edges.SelectMany(e => new[] { e.From, e.To }).Where(id => !known.Contains(id))
            .Select(id => "edge:" + id)
            .Concat(g.Diagnostics.SelectMany(d => d.NodeIds.Where(id => !known.Contains(id)).Select(id => $"{d.Kind}:{id}")))
            .Distinct().ToList();
        Assert.True(dangling.Count == 0, "Unresolved node ids: " + string.Join(", ", dangling));
    }
}
