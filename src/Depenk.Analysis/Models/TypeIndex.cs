using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Depenk.Analysis.Models;

public sealed record TypeDecl(SourceSet Source, SourceDoc Doc, BaseTypeDeclarationSyntax Node, string FullName)
{
    /// <summary>Every declaration part (partial types), in path then position order. The first is Doc/Node.</summary>
    public IReadOnlyList<(SourceDoc Doc, BaseTypeDeclarationSyntax Node)> Parts { get; init; } = [(Doc, Node)];

    public int Arity => (Node as TypeDeclarationSyntax)?.TypeParameterList?.Parameters.Count ?? 0;
}

public sealed class TypeIndex
{
    private readonly ILookup<string, TypeDecl> _byName;

    public TypeIndex(IEnumerable<SourceSet> sources)
    {
        var parts = sources
            .SelectMany(s => s.All<BaseTypeDeclarationSyntax>()
                .Where(t => t.Node is ClassDeclarationSyntax or RecordDeclarationSyntax or StructDeclarationSyntax or EnumDeclarationSyntax)
                .Select(t => (Source: s, t.Doc, t.Node, FullName: FullNameOf(t.Node))))
            .OrderBy(p => p.Doc.RelativePath, StringComparer.Ordinal)
            .ThenBy(p => p.Node.SpanStart)
            .ToList();

        _byName = parts
            .GroupBy(p => (p.Source.ProjectId, p.FullName))
            .Select(g =>
            {
                var first = g.First();
                return new TypeDecl(first.Source, first.Doc, first.Node, first.FullName)
                {
                    Parts = g.Select(p => (p.Doc, p.Node)).ToList(),
                };
            })
            .OrderBy(d => d.Doc.RelativePath, StringComparer.Ordinal)
            .ThenBy(d => d.Node.SpanStart)
            .ToLookup(d => d.Node.Identifier.Text, StringComparer.Ordinal);
    }

    public IEnumerable<TypeDecl> All => _byName.SelectMany(g => g);

    public TypeDecl? Resolve(string simpleName, string fromProjectId, IReadOnlyList<string> referencedProjectIds) =>
        ResolveWithCandidates(simpleName, fromProjectId, referencedProjectIds, null)?.Decl;

    /// <summary>Resolves and also returns every candidate at the winning level (more than one means ambiguous).
    /// When <paramref name="arity"/> is given and some candidates have that generic arity, only those are considered.</summary>
    public (TypeDecl Decl, IReadOnlyList<TypeDecl> Candidates)? ResolveWithCandidates(
        string simpleName, string fromProjectId, IReadOnlyList<string> referencedProjectIds, int? arity)
    {
        var all = _byName[simpleName].ToList();
        if (arity is { } a && all.Any(c => c.Arity == a)) all = all.Where(c => c.Arity == a).ToList();
        if (all.Count == 0) return null;
        var fromRepo = RepoOf(fromProjectId);

        List<TypeDecl> level = all.Where(c => c.Source.ProjectId == fromProjectId).ToList();
        if (level.Count == 0)
            foreach (var r in referencedProjectIds)
            {
                level = all.Where(c => c.Source.ProjectId == r).ToList();
                if (level.Count > 0) break;
            }
        if (level.Count == 0) level = all.Where(c => c.Source.Repo == fromRepo).ToList();
        if (level.Count == 0) level = all;
        return (level[0], level);
    }

    /// <summary>"proj:{repo}/{name}" to repo.</summary>
    private static string RepoOf(string projectId) => projectId["proj:".Length..].Split('/')[0];

    public static string FullNameOf(BaseTypeDeclarationSyntax t)
    {
        var parts = new List<string> { NameWithArity(t) };
        foreach (var anc in t.Ancestors())
        {
            switch (anc)
            {
                case BaseTypeDeclarationSyntax outer: parts.Add(NameWithArity(outer)); break;
                case BaseNamespaceDeclarationSyntax ns: parts.Add(ns.Name.ToString()); break;
            }
        }
        parts.Reverse();
        return string.Join('.', parts);
    }

    private static string NameWithArity(BaseTypeDeclarationSyntax t) =>
        t is TypeDeclarationSyntax { TypeParameterList: { } tp } ? $"{t.Identifier.Text}`{tp.Parameters.Count}" : t.Identifier.Text;
}
