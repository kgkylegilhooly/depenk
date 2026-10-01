namespace Depenk.Core.Model;

public sealed record Edge(EdgeKind Kind, string From, string To, Confidence Confidence)
{
    public string? Version { get; init; }           // references / produces
    public string? Strategy { get; init; }          // targets
    public string? Source { get; init; }            // accepts: body|query|route|header
    public int? StatusCode { get; init; }           // returns
    public string? FieldName { get; init; }         // fieldOf
    public List<string>? ViaPackages { get; init; } // dependsOn
    public int? CallCount { get; init; }            // dependsOn
}
