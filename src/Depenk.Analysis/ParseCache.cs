using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Depenk.Analysis;

public sealed class ParseCache
{
    private readonly ConcurrentDictionary<string, (string Hash, SyntaxTree Tree)> _trees = new(StringComparer.OrdinalIgnoreCase);
    private int _parseCount;

    public int ParseCount => Volatile.Read(ref _parseCount);

    public (SyntaxTree Tree, string Hash) GetOrParse(string absolutePath, string relativePath)
    {
        var text = File.ReadAllText(absolutePath);
        var hash = HashText(text);
        if (_trees.TryGetValue(absolutePath, out var hit) && hit.Hash == hash) return (hit.Tree, hash);
        var tree = CSharpSyntaxTree.ParseText(text, path: relativePath);
        Interlocked.Increment(ref _parseCount);
        _trees[absolutePath] = (hash, tree);
        return (tree, hash);
    }

    public static string HashText(string text) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
}
