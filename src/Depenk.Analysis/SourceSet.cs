using Depenk.Scanning;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Depenk.Analysis;

public sealed record SourceDoc(string RelativePath, SyntaxTree Tree);

public sealed class SourceSet(string repo, string projectId, string projectName, IReadOnlyList<SourceDoc> docs)
{
    public string Repo { get; } = repo;
    public string ProjectId { get; } = projectId;
    public string ProjectName { get; } = projectName;
    public IReadOnlyList<SourceDoc> Docs { get; } = docs;

    public static SourceSet Load(string workspace, string repo, string projectId, string projectName, string projectDir,
        Action<string, Exception> onError)
    {
        var docs = new List<SourceDoc>();
        foreach (var file in PathUtil.EnumerateFiles(projectDir, "*.cs").Order(StringComparer.Ordinal))
        {
            var rel = PathUtil.Rel(workspace, file);
            try { docs.Add(new SourceDoc(rel, CSharpSyntaxTree.ParseText(File.ReadAllText(file), path: rel))); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { onError(rel, ex); }
        }
        return new SourceSet(repo, projectId, projectName, docs);
    }

    public IEnumerable<(SourceDoc Doc, T Node)> All<T>() where T : SyntaxNode =>
        Docs.SelectMany(d => d.Tree.GetRoot().DescendantNodes().OfType<T>().Select(n => (d, n)));
}
