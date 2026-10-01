using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Depenk.Analysis.Models;

public sealed record TypeRef(string Name, bool Collection);

public static class TypeUnwrapper
{
    private static readonly HashSet<string> PassThrough =
        ["Task", "ValueTask", "ActionResult", "Ok", "Created", "CreatedAtRoute", "Accepted", "Results", "Nullable", "IAsyncEnumerable"];
    private static readonly HashSet<string> Collections =
        ["IEnumerable", "List", "IList", "ICollection", "IReadOnlyList", "IReadOnlyCollection", "HashSet", "ISet"];
    private static readonly HashSet<string> Dictionaries = ["Dictionary", "IDictionary", "IReadOnlyDictionary"];
    private static readonly HashSet<string> SimpleNames =
    [
        "string", "int", "long", "short", "byte", "sbyte", "uint", "ulong", "ushort", "bool", "decimal", "double", "float",
        "char", "object", "dynamic", "void", "String", "Int32", "Int64", "Boolean", "Decimal", "Double", "Object",
        "Guid", "DateTime", "DateTimeOffset", "DateOnly", "TimeOnly", "TimeSpan", "Uri", "Stream", "IFormFile",
        "JsonElement", "JsonDocument", "CancellationToken",
        "IActionResult", "ActionResult", "IResult", "NotFound", "NoContent", "BadRequest", "Ok", "Unauthorized",
        "Forbid", "Conflict", "UnprocessableEntity", "ValidationProblem", "ProblemDetails", "HttpResponseMessage", "Task", "ValueTask",
    ];

    public static bool IsSimple(string simpleName) => SimpleNames.Contains(simpleName);

    public static List<TypeRef> Unwrap(string typeText)
    {
        var result = new List<TypeRef>();
        Walk(SyntaxFactory.ParseTypeName(typeText.Replace("global::", "")), false, result);
        return result.Distinct().ToList();
    }

    private static void Walk(TypeSyntax t, bool collection, List<TypeRef> acc)
    {
        switch (t)
        {
            case NullableTypeSyntax n: Walk(n.ElementType, collection, acc); break;
            case ArrayTypeSyntax a: Walk(a.ElementType, true, acc); break;
            case QualifiedNameSyntax q: Walk(q.Right, collection, acc); break;
            case AliasQualifiedNameSyntax al: Walk(al.Name, collection, acc); break;
            case GenericNameSyntax g:
                var name = g.Identifier.Text;
                var args = g.TypeArgumentList.Arguments;
                if (PassThrough.Contains(name)) foreach (var a in args) Walk(a, collection, acc);
                else if (Collections.Contains(name)) foreach (var a in args) Walk(a, true, acc);
                else if (Dictionaries.Contains(name)) { if (args.Count == 2) Walk(args[1], true, acc); }
                else
                {
                    acc.Add(new TypeRef(name, collection));
                    foreach (var a in args) Walk(a, collection, acc);
                }
                break;
            case IdentifierNameSyntax id when !IsSimple(id.Identifier.Text):
                acc.Add(new TypeRef(id.Identifier.Text, collection));
                break;
        }
    }
}
