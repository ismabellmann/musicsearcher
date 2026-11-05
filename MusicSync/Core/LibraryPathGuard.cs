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

        return fullPath;
    }

    public static string NormalizeRelative(string libraryRoot, string fullPath)
    {
        var root = Path.GetFullPath(libraryRoot);
        return Path.GetRelativePath(root, Path.GetFullPath(fullPath)).Replace('\\', '/');
    }
}
