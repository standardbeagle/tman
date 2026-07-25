using System.Security.Cryptography;
using System.Text;

namespace Tman;

/// <summary>
/// Identity of a run for the purposes of dedup locking and parallel-slot counting.
/// A run belongs to the bucket "&lt;name-or-command&gt;@&lt;scope dir&gt;", so a `test` run in
/// one project neither blocks nor consumes a slot from a `test` run in another.
/// </summary>
public static class RunKey
{
    /// <summary>Scope directory for a run: the .tman.kdl dir when one governs it, else the cwd.</summary>
    public static string ScopeDir(TmanConfig? config, string? cwd = null) =>
        config?.Dir ?? cwd ?? Directory.GetCurrentDirectory();

    /// <summary>
    /// Bucket key. Named runs bucket by name; unnamed runs bucket by command basename, so
    /// `npm` and `/usr/bin/npm` in the same directory share a bucket.
    /// </summary>
    public static string For(string? name, string command, string scopeDir) =>
        $"{name ?? Canon.CommandLabel(command)}@{Canon.Dir(scopeDir)}";

    /// <summary>
    /// Filesystem-safe lock file stem. The readable prefix keeps `ls ~/.tman/runs` diagnosable;
    /// the hash suffix keeps two keys that sanitize alike from sharing a lock.
    /// </summary>
    public static string LockStem(string key)
    {
        var prefix = new string(key.TakeWhile(c => c != '@')
            .Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' or '.' ? c : '_')
            .Take(32)
            .ToArray());
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key))).ToLowerInvariant()[..8];
        return $"{prefix}-{hash}";
    }
}
