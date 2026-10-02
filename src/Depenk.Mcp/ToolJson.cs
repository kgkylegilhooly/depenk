using System.Text.Json;
using Depenk.Core;
using Depenk.Query;

namespace Depenk.Mcp;

public static class ToolJson
{
    /// <summary>GraphJson conventions (camelCase, camelCase enums, nulls omitted) without indentation.</summary>
    public static readonly JsonSerializerOptions Compact = new(GraphJson.Options) { WriteIndented = false };

    public static string Envelope<T>(string summary, bool stale, T data) =>
        JsonSerializer.Serialize(new EnvelopeBody<T>(summary, stale, data is ITruncatable t && t.Truncated, data), Compact);

    public static string Error(QueryException e) => Error(e.Code, e.Message, e.Hint, e.Suggestions);

    public static string Error(string code, string message, string? hint, IReadOnlyList<string>? suggestions = null) =>
        JsonSerializer.Serialize(new ErrorBody(code, message, hint, suggestions ?? []), Compact);

    private sealed record EnvelopeBody<T>(string Summary, bool Stale, bool Truncated, T Data);
    private sealed record ErrorBody(string Code, string Message, string? Hint, IReadOnlyList<string> Suggestions);
}
