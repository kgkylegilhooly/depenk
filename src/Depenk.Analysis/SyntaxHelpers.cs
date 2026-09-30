using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Depenk.Analysis;

public static class SyntaxHelpers
{
    public static string AttrName(AttributeSyntax a)
    {
        var name = a.Name switch
        {
            QualifiedNameSyntax q => q.Right.Identifier.Text,
            GenericNameSyntax g => g.Identifier.Text,
            AliasQualifiedNameSyntax al => al.Name.Identifier.Text,
            _ => a.Name.ToString(),
        };
        return name.EndsWith("Attribute", StringComparison.Ordinal) ? name[..^"Attribute".Length] : name;
    }

    public static IEnumerable<AttributeSyntax> Attrs(MemberDeclarationSyntax m) =>
        m.AttributeLists.SelectMany(l => l.Attributes);

    public static IEnumerable<AttributeSyntax> Attrs(ParameterSyntax p) => p.AttributeLists.SelectMany(l => l.Attributes);

    public static string? FirstStringArg(AttributeSyntax a) =>
        a.ArgumentList?.Arguments.Where(x => x.NameEquals is null && (x.NameColon is null || x.NameColon.Name.Identifier.Text == "template"))
            .Select(x => StringValue(x.Expression)).FirstOrDefault(s => s is not null);

    public static int Line(SyntaxNode n) => n.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
    public static int Line(SyntaxToken t) => t.GetLocation().GetLineSpan().StartLinePosition.Line + 1;

    public static string TypeName(TypeSyntax t) => t.ToString().Replace("global::", "");

    /// <summary>Best-effort static string value of an expression.</summary>
    public static string? StringValue(ExpressionSyntax e) => StringValue(e, 0);

    private static string? StringValue(ExpressionSyntax e, int depth) => depth > 10 ? null : e switch
    {
        LiteralExpressionSyntax l when l.IsKind(SyntaxKind.StringLiteralExpression) => l.Token.ValueText,
        InterpolatedStringExpressionSyntax i => string.Concat(i.Contents.Select(c => c switch
        {
            InterpolatedStringTextSyntax t => t.TextToken.ValueText,
            InterpolationSyntax h => "{" + HoleName(h.Expression) + "}",
            _ => "",
        })),
        BinaryExpressionSyntax b when b.IsKind(SyntaxKind.AddExpression) =>
            (StringValue(b.Left, depth + 1) ?? "{}") + (StringValue(b.Right, depth + 1) ?? "{}"),
        IdentifierNameSyntax or MemberAccessExpressionSyntax => ConstValue(e, depth),
        _ => null,
    };

    private static string HoleName(ExpressionSyntax e) => e switch
    {
        IdentifierNameSyntax id => id.Identifier.Text,
        MemberAccessExpressionSyntax m => m.Name.Identifier.Text,
        _ => "",
    };

    /// <summary>Resolves `Name` or `Type.Name` to a const string declared in the same syntax tree.</summary>
    private static string? ConstValue(ExpressionSyntax e, int depth)
    {
        var name = e switch
        {
            IdentifierNameSyntax id => id.Identifier.Text,
            MemberAccessExpressionSyntax m => m.Name.Identifier.Text,
            _ => null,
        };
        if (name is null) return null;
        var declarator = e.SyntaxTree.GetRoot().DescendantNodes().OfType<VariableDeclaratorSyntax>()
            .FirstOrDefault(v => v.Identifier.Text == name
                                 && v.Parent?.Parent is FieldDeclarationSyntax f
                                 && f.Modifiers.Any(SyntaxKind.ConstKeyword));
        return declarator?.Initializer?.Value is { } init && !ReferenceEquals(init, e) ? StringValue(init, depth + 1) : null;
    }
}
