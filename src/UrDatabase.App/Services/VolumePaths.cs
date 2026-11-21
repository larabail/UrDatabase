using System;
using System.IO;
using System.Linq;

namespace UrDatabase.Services;

public static class VolumePaths
{
    public static bool IsWindows(string path) =>
        path.Length >= 3 && char.IsAsciiLetter(path[0]) && path[1] == ':' && path[2] is '\\' or '/'
        || path.StartsWith(@"\\", StringComparison.Ordinal) || path.StartsWith("//", StringComparison.Ordinal);

    public static StringComparison Comparison(string path, string? style = null) =>
        IsWindows(path) || style is "windows" or "posix-insensitive" ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    public static string Absolute(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("An absolute location is required.");
        var windows = IsWindows(path);
        var value = windows ? path.Replace('\\', '/') : path;
        if (!windows && !value.StartsWith('/'))
            throw new ArgumentException("A relative location cannot identify a disk.");
        var prefix = windows && value[1] == ':' ? char.ToUpperInvariant(value[0]) + ":/"
            : value.StartsWith("//", StringComparison.Ordinal) ? "//" : "/";
        var parts = value[prefix.Length..].Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Any(p => p is "." or ".." || p.Contains('\0')))
            throw new ArgumentException("Locations containing traversal or invalid segments cannot be bound.");
        if (prefix == "//" && parts.Length < 2)
            throw new ArgumentException("A network location must include its share.");
        var result = prefix + string.Join('/', parts);
        return windows ? result.Replace('/', '\\') : result;
    }

    public static bool TryRelative(string root, string path, out string relative, string? style = null)
    {
        relative = "";
        var parent = Absolute(root);
        var child = Absolute(path);
        if (IsWindows(parent) != IsWindows(child)) return false;
        var comparison = Comparison(parent, style);
        if (child.Equals(parent, comparison)) return true;
        var separator = IsWindows(parent) ? '\\' : '/';
        var prefix = parent.EndsWith(separator) ? parent : parent + separator;
        if (!child.StartsWith(prefix, comparison)) return false;
        relative = child[prefix.Length..].Replace(separator, '/');
        return true;
    }

    public static string Join(string root, string relative)
    {
        var parent = Absolute(root);
        if (relative.Contains('\\') || relative.StartsWith('/') || relative.Contains('\0')
            || relative.Length >= 2 && char.IsAsciiLetter(relative[0]) && relative[1] == ':'
            || relative.Split('/').Any(s => s is "." or ".." || IsWindows(parent) && s.Contains(':') || relative.Length > 0 && s.Length == 0))
            throw new ArgumentException("The saved relative path is invalid.");
        if (relative.Length == 0) return parent;
        var separator = IsWindows(parent) ? '\\' : '/';
        var result = parent.TrimEnd(separator) + separator + relative.Replace('/', separator);
        if (!TryRelative(parent, result, out _))
            throw new ArgumentException("The saved relative path escapes its disk.");
        return result;
    }

    public static string RelativeKey(string relative, string style) =>
        style is "windows" or "posix-insensitive" ? relative.ToUpperInvariant() : relative;

    public static string Style(string path) => IsWindows(path) ? "windows" : "posix";

    public static void CheckLinks(string root, string path)
    {
        if (!TryRelative(root, path, out var relative))
            throw new IOException("The location is outside its verified disk.");
        var current = root;
        foreach (var segment in relative.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            FileSystemInfo entry = Directory.Exists(current) ? new DirectoryInfo(current) : new FileInfo(current);
            if (entry.LinkTarget is null) continue;
            var target = entry.ResolveLinkTarget(true)
                ?? throw new IOException("A symbolic link could not be resolved.");
            if (!TryRelative(root, target.FullName, out _))
                throw new IOException("A symbolic link leaves the verified disk. Choose the real folder instead.");
        }
    }
}
