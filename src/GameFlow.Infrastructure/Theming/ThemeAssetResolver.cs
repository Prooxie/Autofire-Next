using System.Collections.Concurrent;

namespace GameFlow.Infrastructure.Theming;

/// <summary>
/// Turns an <c>image</c> reference from a theme manifest into an absolute
/// path on disk.
///
/// <para>
/// This is deliberately forgiving, because theme packs are third-party
/// data that routinely disagrees with itself. VSCView packs address art
/// root-relatively (<c>\dualsense\Image Assets\x.png</c>), which only
/// resolves if the whole VSCView tree was copied verbatim under the same
/// folder names. Renaming a pack folder — which is what happened when the
/// bundled packs gained their <c>-default</c> suffix — invalidates every
/// such path at once even though the art is still sitting right where the
/// pack expects it, relative to the pack. Packs also disagree about their
/// own asset folder: several manifests ask for <c>assets\</c> or
/// <c>Theme Assets\</c> while shipping <c>Image Assets\</c>.
/// </para>
///
/// <para>
/// The alternative to being forgiving is a controller that renders
/// completely blank, which is what the user actually sees when any one
/// of these mismatches occurs.
/// </para>
/// </summary>
public static class ThemeAssetResolver
{
    /// <summary>
    /// How far up the directory chain the ancestor probe walks. A variant
    /// theme sits at <c>themes/&lt;pack&gt;/&lt;variant&gt;/</c>, so two
    /// levels covers the shipped layouts; the extra headroom costs
    /// nothing because the walk stops at the themes root regardless.
    /// </summary>
    private const int MaxAncestorDepth = 4;

    private static readonly ConcurrentDictionary<string, string?> PackFileCache = new();

    /// <summary>
    /// Resolves <paramref name="imagePath"/> to an existing file, or
    /// <see langword="null"/> when nothing matches.
    /// </summary>
    /// <param name="imagePath">The raw value from the manifest.</param>
    /// <param name="baseDirectory">Folder holding the theme.json.</param>
    /// <param name="themesRoot">The themes root; <c>\</c>-prefixed paths are relative to this.</param>
    public static string? Resolve(string? imagePath, string? baseDirectory, string? themesRoot)
    {
        if (string.IsNullOrWhiteSpace(imagePath) || string.IsNullOrEmpty(baseDirectory))
        {
            return null;
        }

        var rootFull = string.IsNullOrEmpty(themesRoot) ? null : Path.GetFullPath(themesRoot);
        var rooted = imagePath.StartsWith('\\') || imagePath.StartsWith('/');
        var relative = imagePath.TrimStart('\\', '/')
            .Replace('/', Path.DirectorySeparatorChar)
            .Replace('\\', Path.DirectorySeparatorChar);

        // 1. The path as written.
        if (rooted)
        {
            if (rootFull is not null && Exists(Path.Combine(rootFull, relative), out var asWritten))
            {
                return asWritten;
            }
        }
        else if (Exists(Path.Combine(baseDirectory, relative), out var direct))
        {
            return direct;
        }

        var segments = relative.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0)
        {
            return null;
        }

        // 2. Ancestor probe. The first segment of a root-relative path
        //    names the pack folder, so also try it with that segment
        //    dropped — that is precisely what a pack-folder rename breaks.
        var withoutPack = segments.Length >= 2
            ? string.Join(Path.DirectorySeparatorChar, segments[1..])
            : null;

        var probe = baseDirectory;
        for (var depth = 0; depth < MaxAncestorDepth && !string.IsNullOrEmpty(probe); depth++)
        {
            if (Exists(Path.Combine(probe, relative), out var viaAncestor))
            {
                return viaAncestor;
            }

            if (withoutPack is not null && Exists(Path.Combine(probe, withoutPack), out var viaTrimmed))
            {
                return viaTrimmed;
            }

            if (rootFull is not null &&
                string.Equals(Path.GetFullPath(probe), rootFull, StringComparison.OrdinalIgnoreCase))
            {
                break;
            }

            probe = Path.GetDirectoryName(probe);
        }

        // 3. Filename search, confined to the theme's own pack. Confining
        //    it is what makes this safe: it can pick a different SUBFOLDER
        //    of the same controller pack, never art from another
        //    controller.
        return FindInPack(baseDirectory, rootFull, segments[^1]);
    }

    /// <summary>
    /// The theme's pack root — its top-level folder under the themes root.
    /// For <c>themes/dualshock-4-default/DS4 V2 Gold/</c> that is
    /// <c>themes/dualshock-4-default/</c>.
    /// </summary>
    public static string GetPackRoot(string baseDirectory, string? themesRoot)
    {
        if (string.IsNullOrEmpty(themesRoot))
        {
            return baseDirectory;
        }

        var root = Path.GetFullPath(themesRoot);
        var current = Path.GetFullPath(baseDirectory);

        while (!string.IsNullOrEmpty(current))
        {
            var parent = Path.GetDirectoryName(current);
            if (parent is null)
            {
                return baseDirectory;
            }

            if (string.Equals(parent, root, StringComparison.OrdinalIgnoreCase))
            {
                return current;
            }

            current = parent;
        }

        return baseDirectory;
    }

    private static string? FindInPack(string baseDirectory, string? themesRoot, string fileName)
    {
        var packRoot = GetPackRoot(baseDirectory, themesRoot);
        var key = $"{packRoot}|{fileName}";

        return PackFileCache.GetOrAdd(key, _ =>
        {
            try
            {
                return Directory
                    .EnumerateFiles(packRoot, fileName, SearchOption.AllDirectories)
                    .FirstOrDefault();
            }
            catch (Exception)
            {
                // A pack folder that vanished mid-session, or a path the
                // filesystem rejects. Not resolvable is the right answer.
                return null;
            }
        });
    }

    private static bool Exists(string candidate, out string resolved)
    {
        try
        {
            resolved = Path.GetFullPath(candidate);
            return File.Exists(resolved);
        }
        catch (Exception)
        {
            // Manifest paths are third-party data and can contain
            // characters this platform rejects outright.
            resolved = string.Empty;
            return false;
        }
    }

    /// <summary>Drops cached pack lookups. For tests and theme reinstalls.</summary>
    public static void ClearCache() => PackFileCache.Clear();
}
