namespace Depenk.Core;

public static class AtomicFile
{
    /// <summary>
    /// Writes to a sibling temp file, then renames it over <paramref name="path"/>, so readers and crashes only ever see
    /// the old or the new contents, never a half-written file.
    /// </summary>
    public static void WriteAllText(string path, string contents)
    {
        var full = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        var temp = $"{full}.{Guid.NewGuid():N}.tmp"; // unique: a CLI scan and an MCP server may save concurrently
        try
        {
            File.WriteAllText(temp, contents);
            File.Move(temp, full, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
    }
}
