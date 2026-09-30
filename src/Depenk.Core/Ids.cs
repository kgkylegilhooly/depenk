namespace Depenk.Core;

public static class Ids
{
    public static string Repo(string repo) => $"repo:{repo}";
    public static string Project(string repo, string project) => $"proj:{repo}/{project}";
    public static string Package(string packageId) => $"pkg:{packageId}";
    public static string Endpoint(string repo, string verb, string route) => $"ep:{repo}:{verb.ToUpperInvariant()}:{route}";
    public static string ClientMethod(string project, string type, string method) => $"cm:{project}:{type}.{method}";
    public static string CallSite(string repo, string project, string type, string member, int line) =>
        $"cs:{repo}/{project}:{type}.{member}:{line}";
    public static string Model(string project, string fullName) => $"model:{project}:{fullName}";
}
