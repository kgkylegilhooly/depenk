using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using static Depenk.Analysis.SyntaxHelpers;

namespace Depenk.Analysis;

public static class DeclaredTypes
{
    public static string? Of(ExpressionSyntax receiver, SyntaxNode context)
    {
        var name = receiver switch
        {
            IdentifierNameSyntax id => id.Identifier.Text,
            MemberAccessExpressionSyntax { Expression: ThisExpressionSyntax } m => m.Name.Identifier.Text,
            _ => null,
        };
        if (name is null) return null;

        foreach (var anc in context.Ancestors())
        {
            if (anc is BaseMethodDeclarationSyntax or LocalFunctionStatementSyntax or AnonymousFunctionExpressionSyntax
                or AccessorDeclarationSyntax or GlobalStatementSyntax)
            {
                var local = anc.DescendantNodes().OfType<VariableDeclaratorSyntax>()
                    .FirstOrDefault(v => v.Identifier.Text == name && v.Parent is VariableDeclarationSyntax);
                if (local?.Parent is VariableDeclarationSyntax decl)
                    return decl.Type.IsVar ? FromInitializer(local.Initializer?.Value) : TypeName(decl.Type);

                IEnumerable<ParameterSyntax> parameters = anc switch
                {
                    BaseMethodDeclarationSyntax bm => bm.ParameterList.Parameters,
                    LocalFunctionStatementSyntax lf => lf.ParameterList.Parameters,
                    ParenthesizedLambdaExpressionSyntax pl => pl.ParameterList.Parameters,
                    _ => [],
                };
                if (parameters.FirstOrDefault(p => p.Identifier.Text == name) is { Type: { } pt }) return TypeName(pt);
            }
            if (anc is TypeDeclarationSyntax td)
            {
                if (td.ParameterList?.Parameters.FirstOrDefault(p => p.Identifier.Text == name) is { Type: { } ct })
                    return TypeName(ct);
                foreach (var f in td.Members.OfType<FieldDeclarationSyntax>())
                    if (f.Declaration.Variables.Any(v => v.Identifier.Text == name)) return TypeName(f.Declaration.Type);
                foreach (var p in td.Members.OfType<PropertyDeclarationSyntax>())
                    if (p.Identifier.Text == name) return TypeName(p.Type);
                return null;
            }
        }
        return null;
    }

    public static List<string> BaseTypes(TypeDeclarationSyntax td) =>
        td.BaseList?.Types.Select(t => TypeName(t.Type)).ToList() ?? [];

    private static string? FromInitializer(ExpressionSyntax? init) => init switch
    {
        ObjectCreationExpressionSyntax oc => TypeName(oc.Type),
        InvocationExpressionSyntax { Expression: MemberAccessExpressionSyntax { Name: GenericNameSyntax g } }
            when g.Identifier.Text is "GetRequiredService" or "GetService" => TypeName(g.TypeArgumentList.Arguments[0]),
        AwaitExpressionSyntax a => FromInitializer(a.Expression),
        _ => null,
    };
}
