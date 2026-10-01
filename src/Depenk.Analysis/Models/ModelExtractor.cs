using Depenk.Core;
using Depenk.Core.Model;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using static Depenk.Analysis.SyntaxHelpers;

namespace Depenk.Analysis.Models;

public sealed class ModelExtractor(
    IReadOnlyList<SourceSet> sources, Func<string, IReadOnlyList<string>> referencedProjectIds, int maxDepth = 6)
{
    private readonly TypeIndex _index = new(sources);

    /// <param name="clientTypes">(projectId, simple type name) of types that own client methods; they are never seeded as contract models.</param>
    /// <param name="onError">Called with the project id when analysing one endpoint or contract type throws; extraction continues.</param>
    public void Extract(DepGraph graph, IReadOnlySet<string> contractProjectIds,
        IReadOnlySet<(string ProjectId, string TypeName)>? clientTypes = null, Action<string, Exception>? onError = null)
    {
        var models = new Dictionary<string, ModelNode>(StringComparer.Ordinal);
        foreach (var m in graph.Models) models.TryAdd(m.Id, m);
        // model id -> (project, full name) of the declaration that owns it; same-named projects in different repos
        // share ProjectName, so a second declaration with the same base id gets "#N".
        var owners = models.ToDictionary(kv => kv.Key, kv => (kv.Value.ProjectId, kv.Value.FullName), StringComparer.Ordinal);
        var declIds = new Dictionary<TypeDecl, string>(ReferenceEqualityComparer.Instance);
        var edges = new HashSet<Edge>(graph.Edges);
        var expandedAt = new Dictionary<string, int>();          // model id -> shallowest depth it was expanded from
        var ambiguityReported = new HashSet<(string, string)>();
        void AddEdge(Edge e) { if (edges.Add(e)) graph.Edges.Add(e); }

        string IdOf(TypeDecl decl)
        {
            if (declIds.TryGetValue(decl, out var known)) return known;
            var baseId = Ids.Model(decl.Source.ProjectName, decl.FullName);
            var id = baseId;
            for (var n = 2; owners.TryGetValue(id, out var o) && (o.ProjectId != decl.Source.ProjectId || o.FullName != decl.FullName); n++)
                id = $"{baseId}#{n}";
            owners[id] = (decl.Source.ProjectId, decl.FullName);
            return declIds[decl] = id;
        }

        // Every candidate becomes a node; an ambiguous reference links to each of them with Low confidence.
        List<(string Id, Confidence Confidence)> Ensure(TypeRef tr, string fromProjectId, int depth)
        {
            var resolved = _index.ResolveWithCandidates(tr.Name, fromProjectId, referencedProjectIds(fromProjectId), tr.Arity);
            if (resolved is null)
            {
                var opaqueId = Ids.Model("?", tr.Name);
                if (!models.ContainsKey(opaqueId))
                {
                    models[opaqueId] = new ModelNode(opaqueId, "", null, tr.Name, ModelKind.Opaque, [], null, null);
                    graph.Models.Add(models[opaqueId]);
                }
                return [(opaqueId, Confidence.High)];
            }
            var (decl, candidates) = resolved.Value;
            if (candidates.Count <= 1) return [(EnsureDecl(decl, depth), Confidence.High)];

            var ids = candidates.Select(c => EnsureDecl(c, depth)).ToList();
            if (ambiguityReported.Add((tr.Name, fromProjectId)))
                graph.Diagnostics.Add(new Depenk.Core.Model.Diagnostic(DiagnosticKinds.AmbiguousModel, Severities.Info, ids,
                    $"Type '{tr.Name}' used from {fromProjectId} matches {ids.Count} declarations: {string.Join(", ", ids)}; " +
                    "all are kept as low-confidence candidates."));
            return ids.Select(id => (id, Confidence.Low)).ToList();
        }

        string EnsureDecl(TypeDecl decl, int depth)
        {
            var id = IdOf(decl);
            List<ModelField> fields;
            if (models.TryGetValue(id, out var existing))
            {
                // Already registered; only re-expand when reached at a shallower depth than before.
                if (!expandedAt.TryGetValue(id, out var seen) || depth >= seen) return id;
                fields = existing.Fields;
            }
            else
            {
                var (kind, f, enumValues) = Describe(decl);
                fields = f;
                var node = new ModelNode(id, decl.Source.Repo, decl.Source.ProjectId, decl.FullName, kind, fields, enumValues,
                    new SourceLocation(decl.Doc.RelativePath, Line(decl.Node.Identifier)));
                models[id] = node;          // register before recursing: cycles terminate
                graph.Models.Add(node);
            }
            expandedAt[id] = depth;

            if (depth >= maxDepth) return id;
            var typeParams = decl.Parts.SelectMany(p => (p.Node as TypeDeclarationSyntax)?.TypeParameterList?.Parameters
                                                          .Select(tp => tp.Identifier.Text) ?? []).ToHashSet();
            foreach (var f in fields)
            foreach (var child in TypeUnwrapper.Unwrap(f.TypeName))
            {
                if (typeParams.Contains(child.Name)) continue;
                foreach (var (childId, conf) in Ensure(child, decl.Source.ProjectId, depth + 1))
                    AddEdge(new Edge(EdgeKind.FieldOf, id, childId, conf) { FieldName = f.Name });
            }
            return id;
        }

        foreach (var ep in graph.Endpoints.ToList())
        {
            try
            {
                foreach (var p in ep.Parameters)
                foreach (var tr in TypeUnwrapper.Unwrap(p.TypeName))
                foreach (var (mid, conf) in Ensure(tr, ep.ProjectId, 1))
                    AddEdge(new Edge(EdgeKind.Accepts, ep.Id, mid, conf) { Source = p.Source });
                foreach (var r in ep.Responses)
                foreach (var tr in TypeUnwrapper.Unwrap(r.TypeName))
                foreach (var (mid, conf) in Ensure(tr, ep.ProjectId, 1))
                    AddEdge(new Edge(EdgeKind.Returns, ep.Id, mid, conf) { StatusCode = r.StatusCode });
            }
            catch (Exception ex) when (onError is not null) { onError(ep.ProjectId, ex); }
        }

        foreach (var decl in _index.All.Where(d => contractProjectIds.Contains(d.Source.ProjectId)
                                                  && clientTypes?.Contains((d.Source.ProjectId, d.Node.Identifier.Text)) != true
                                                  && d.Parts.Any(p => p.Node.Modifiers.Any(SyntaxKind.PublicKeyword))))
        {
            try { EnsureDecl(decl, 1); }
            catch (Exception ex) when (onError is not null) { onError(decl.Source.ProjectId, ex); }
        }
    }

    private (ModelKind, List<ModelField>, List<string>?) Describe(TypeDecl decl)
    {
        if (decl.Node is EnumDeclarationSyntax en)
            return (ModelKind.Enum, [], en.Members.Select(m => m.Identifier.Text).ToList());

        var types = decl.Parts.Select(p => p.Node).OfType<TypeDeclarationSyntax>().ToList();
        var kind = types[0] switch
        {
            RecordDeclarationSyntax => ModelKind.Record,
            StructDeclarationSyntax => ModelKind.Struct,
            _ => ModelKind.Class,
        };
        var fields = new List<ModelField>();
        void AddAll(IEnumerable<ModelField> more)
        {
            foreach (var f in more) if (fields.All(x => x.Name != f.Name)) fields.Add(f);
        }
        foreach (var type in types)
        {
            if (type is RecordDeclarationSyntax && type.ParameterList is { } positional)
                AddAll(positional.Parameters.Where(p => p.Type is not null).Select(p => Field(p.Identifier.Text, p.Type!)));
            AddAll(PublicProperties(type));
        }

        var projectId = decl.Source.ProjectId;
        var baseDecl = types.SelectMany(DeclaredTypes.BaseTypes)
            .Select(b => BaseRef(b))
            .Select(b => _index.ResolveWithCandidates(b.Name, projectId, referencedProjectIds(projectId), b.Arity)?.Decl)
            .FirstOrDefault(d => d?.Node is ClassDeclarationSyntax or RecordDeclarationSyntax);
        if (baseDecl is not null)
            foreach (var baseType in baseDecl.Parts.Select(p => p.Node).OfType<TypeDeclarationSyntax>())
                AddAll(PublicProperties(baseType));
        return (kind, fields, null);
    }

    /// <summary>Rightmost simple name of a (possibly qualified/generic) base type text.</summary>
    private static TypeRef BaseRef(string text) =>
        TypeUnwrapper.Unwrap(text).FirstOrDefault() ?? new TypeRef(text.Split('<')[0].Split('.')[^1], false);

    private static IEnumerable<ModelField> PublicProperties(TypeDeclarationSyntax t) =>
        t.Members.OfType<PropertyDeclarationSyntax>()
            .Where(p => p.Modifiers.Any(SyntaxKind.PublicKeyword) && !p.Modifiers.Any(SyntaxKind.StaticKeyword)
                        && (p.ExpressionBody is not null
                            || p.AccessorList?.Accessors.Any(a => a.IsKind(SyntaxKind.GetAccessorDeclaration)) == true))
            .Select(p => Field(p.Identifier.Text, p.Type));

    private static ModelField Field(string name, TypeSyntax type)
    {
        var text = TypeName(type);
        return new ModelField(name, text, text.EndsWith('?'), TypeUnwrapper.Unwrap(text).Any(r => r.Collection));
    }
}
