namespace Depenk.Tests.TestUtil;

public sealed class TempWorkspace : IDisposable
{
    public string Root { get; } = Path.Combine(Path.GetTempPath(), "depenk-tests", Guid.NewGuid().ToString("N"));

    public TempWorkspace() => Directory.CreateDirectory(Root);

    public TempWorkspace File(string relativePath, string content)
    {
        var full = Path.Combine(Root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        System.IO.File.WriteAllText(full, content);
        return this;
    }

    /// <summary>Creates a fake git repo: .git/HEAD pointing at a branch ref with a fixed sha.</summary>
    public TempWorkspace Repo(string name, string sha = "0123456789abcdef0123456789abcdef01234567")
    {
        File($"{name}/.git/HEAD", "ref: refs/heads/main\n");
        File($"{name}/.git/refs/heads/main", sha + "\n");
        return this;
    }

    public void Dispose()
    {
        try { Directory.Delete(Root, recursive: true); } catch (IOException) { /* best effort */ }
    }
}
