using System.Text.Json;
using Depenk.Core;
using Depenk.Query;
using Depenk.Scanning.Config;

namespace Depenk.Mcp;

public static class ToolJson
{
    /// <summary>GraphJson conventions (camelCase, camelCase enums, nulls omitted) without indentation.</summary>
    public static readonly JsonSerializerOptions Compact = new(GraphJson.Options) { WriteIndented = false };

    public static string Envelope<T>(string summary, bool stale, T data) =>
        JsonSerializer.Serialize(new EnvelopeBody<T>(summary, stale, data is ITruncatable t && t.Truncated, data), Compact);

    public static string Error(QueryException e) => Error(e.Code, e.Message, e.Hint, e.Suggestions);

    public const string ScanHint = "Fix depenk.yml or run `depenk scan` to see the full error.";

    /// <summary>
    /// Error body for any tool failure. Unexpected exceptions are blamed on the scan only when they happened while
    /// loading or scanning the graph; otherwise they are depenk bugs and must not send the user to their config.
    /// </summary>
    public static string ErrorFor(Exception e, bool duringScan) => e switch
    {
        QueryException q => Error(q),
        ConfigException c => Error(QueryException.InvalidArgument, c.Message, ScanHint),
        _ when duringScan => Error("scan_failed", e.Message, ScanHint),
        _ => Error("internal_error", e.Message, "This is a depenk bug; please report it with the tool call that failed."),
    };

    public static string Error(string code, string message, string? hint, IReadOnlyList<string>? suggestions = null) =>
        JsonSerializer.Serialize(new ErrorBody(code, message, hint, suggestions ?? []), Compact);

    private sealed record EnvelopeBody<T>(string Summary, bool Stale, bool Truncated, T Data);
    private sealed record ErrorBody(string Code, string Message, string? Hint, IReadOnlyList<string> Suggestions);
}
