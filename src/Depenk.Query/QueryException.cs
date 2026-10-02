namespace Depenk.Query;

public sealed class QueryException(string code, string message, string? hint = null, IReadOnlyList<string>? suggestions = null)
    : Exception(message)
{
    public const string NotFound = "not_found", Ambiguous = "ambiguous", InvalidArgument = "invalid_argument",
        OutsideWorkspace = "outside_workspace";

    public string Code { get; } = code;
    public string? Hint { get; } = hint;
    public IReadOnlyList<string>? Suggestions { get; } = suggestions;
}
