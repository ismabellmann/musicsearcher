namespace MusicSync.Core;

/// <summary>
/// Resolves user-supplied relative paths inside the library root (section 11).
/// </summary>
public static class LibraryPathGuard
{
    public static string ResolveRelativePath(string libraryRoot, string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
            throw new ArgumentException("La ruta no puede estar vacía.", nameof(relativePath));

        var normalized = relativePath.Replace('\\', '/');
        if (Path.IsPathRooted(normalized) || normalized.StartsWith('/'))
            throw new ArgumentException("La ruta debe ser relativa a la biblioteca.", nameof(relativePath));

        if (normalized.Split('/').Any(segment => segment == ".."))
            throw new ArgumentException("La ruta no puede contener '..'.", nameof(relativePath));

        var root = Path.GetFullPath(libraryRoot);
        var fullPath = Path.GetFullPath(Path.Combine(root, normalized));
        if (!fullPath.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new ArgumentException("La ruta queda fuera de la biblioteca.", nameof(relativePath));

        RejectReparsePoints(root, fullPath, relativePath);

        return fullPath;
    }

    private static void RejectReparsePoints(string root, string fullPath, string relativePath)
    {
        var current = root;
        while (true)
        {
            if ((File.Exists(current) || Directory.Exists(current))
                && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new ArgumentException("La ruta no puede atravesar enlaces simbólicos o puntos de montaje reencaminados.", nameof(relativePath));

            if (string.Equals(current, fullPath, StringComparison.Ordinal))
                break;

            var next = Path.Combine(current, Path.GetRelativePath(current, fullPath).Split(Path.DirectorySeparatorChar)[0]);
            if (string.Equals(next, current, StringComparison.Ordinal))
                break;
            current = next;
            if (!Directory.Exists(current) && !File.Exists(current))
                break;
        }
    }

    public static string NormalizeRelative(string libraryRoot, string fullPath)
    {
        var root = Path.GetFullPath(libraryRoot);
        return Path.GetRelativePath(root, Path.GetFullPath(fullPath)).Replace('\\', '/');
    }
}
