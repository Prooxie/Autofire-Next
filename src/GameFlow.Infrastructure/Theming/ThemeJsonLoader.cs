using System.Text.Json;
using GameFlow.Infrastructure.Theming.Flee;
using GameFlow.Infrastructure.Theming.Models;
using Serilog;

// Bring the internal FleeNumber helper into scope so string-encoded
// numeric properties parse with the same invariant culture rules the
// Flee tokenizer uses.
using FleeNumber = GameFlow.Infrastructure.Theming.Flee.FleeNumber;

namespace GameFlow.Infrastructure.Theming;

/// <summary>
/// Loads VSCView-compatible <c>theme.json</c> files into the in-memory
/// <see cref="ThemeDocument"/> model. Schema follows the spec in
/// <see href="https://github.com/Nielk1/VSCView/blob/master/THEMEENGINE.md">VSCView's
/// THEMEENGINE.md</see>; this loader is deliberately permissive — unknown
/// fields are logged at <see cref="Serilog.Events.LogEventLevel.Debug"/>
/// and ignored, so future VSCView elements don't break older clients.
///
/// <para>
/// The loader is allocation-conscious: it deserialises into a
/// <see cref="JsonDocument"/> once, walks the tree to build typed nodes,
/// then disposes the JSON document so only the typed tree survives. All
/// Flee expressions are pre-parsed during load and cached on the matching
/// node; the render path never touches the source strings.
/// </para>
/// </summary>
public static class ThemeJsonLoader
{
    /// <summary>
    /// Loads the theme.json at <paramref name="path"/>. Throws
    /// <see cref="ThemeLoadException"/> for malformed JSON or unsupported
    /// schema; lets <see cref="IOException"/> bubble for unreachable paths.
    ///
    /// <para>
    /// When <paramref name="explicitThemesRoot"/> is provided, that path
    /// is used as the document's
    /// <see cref="ThemeDocument.ThemesRootDirectory"/>. Otherwise the
    /// loader falls back to "one directory above the theme.json's
    /// folder" — which only works for the legacy one-level layout
    /// (themes/&lt;style&gt;/theme.json). For nested variant folders
    /// (themes/&lt;style&gt;/&lt;variant&gt;/theme.json or deeper) the
    /// caller MUST supply the explicit root, otherwise <c>\</c>-prefixed
    /// image paths will resolve relative to the wrong directory and
    /// every load will fail with "image not found on disk".
    /// </para>
    /// </summary>
    public static ThemeDocument LoadFromFile(string path, string? explicitThemesRoot = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        if (!File.Exists(path))
        {
            throw new ThemeLoadException($"Theme file not found: {path}");
        }

        var json = File.ReadAllText(path);
        var baseDir = Path.GetDirectoryName(Path.GetFullPath(path));

        // Prefer the caller-supplied root (the registry knows the
        // actual top-level themes folder regardless of how many
        // intermediate variant directories sit between it and this
        // theme.json). Fall back to the legacy "one above baseDir"
        // heuristic only when no explicit root is given — that path is
        // here purely for the (admittedly rare) standalone-loader case.
        var themesRoot = explicitThemesRoot is not null
            ? Path.GetFullPath(explicitThemesRoot)
            : baseDir is null ? null : Path.GetDirectoryName(baseDir);

        return LoadFromString(json, baseDirectory: baseDir, themesRootDirectory: themesRoot);
    }

    /// <summary>
    /// Parses <paramref name="json"/> as theme data. <paramref name="baseDirectory"/>
    /// is stored on the resulting <see cref="ThemeDocument.BaseDirectory"/>
    /// so image paths resolve correctly; pass <see langword="null"/> for
    /// in-memory documents that never need filesystem resolution.
    /// </summary>
    public static ThemeDocument LoadFromString(
        string json,
        string? baseDirectory = null,
        string? themesRootDirectory = null)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new ThemeLoadException("theme.json must be a JSON object at the top level.");
        }

        var name      = GetString(root, "name") ?? "Untitled";
        var width     = GetDouble(root, "width", 1500);
        var height    = GetDouble(root, "height", 900);
        var version   = (int)GetDouble(root, "version", 1);
        var children  = root.TryGetProperty("children", out var childArr)
                        ? ParseChildren(childArr)
                        : [];

        return new ThemeDocument
        {
            Name = name,
            Width = width,
            Height = height,
            Version = version,
            Children = children,
            BaseDirectory = baseDirectory,
            ThemesRootDirectory = themesRootDirectory
        };
    }

    // ─── Element parsing ──────────────────────────────────────────────────────

    /// <summary>
    /// Parses a JSON array of child nodes. The element <c>type</c> field
    /// drives the typed-node selection; missing or null types fall through
    /// to <see cref="GroupNode"/> per VSCView semantics.
    /// </summary>
    private static List<ThemeNode> ParseChildren(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var list = new List<ThemeNode>(element.GetArrayLength());
        foreach (var item in element.EnumerateArray())
        {
            try
            {
                list.Add(ParseNode(item));
            }
            catch (Exception ex)
            {
                // Log and skip — a single malformed child shouldn't take
                // out the whole theme. Authors get loud feedback in the
                // log file without losing 90 % of their controller art.
                Log.Warning(ex, "Skipping malformed theme node.");
            }
        }
        return list;
    }

    private static ThemeNode ParseNode(JsonElement element)
    {
        var type = GetString(element, "type")?.ToLowerInvariant();

        // Shared "Item" fields populated on every node.
        var x   = GetDouble(element, "x", 0);
        var y   = GetDouble(element, "y", 0);
        var rot = GetDouble(element, "rot", 0);
        var children = element.TryGetProperty("children", out var ce)
                       ? ParseChildren(ce)
                       : [];

        switch (type)
        {
            case null:
            case "":
            case "item":
                return new GroupNode
                {
                    X = x, Y = y, Rotation = rot, Children = children
                };

            case "image":
                return new ImageNode
                {
                    X = x, Y = y, Rotation = rot, Children = children,
                    ImagePath = GetString(element, "image") ?? string.Empty,
                    Width     = GetDouble(element, "width",  0),
                    Height    = GetDouble(element, "height", 0),
                    Center    = GetBool(element, "center", false)
                };

            case "lightbar":
                return new LightbarNode
                {
                    X = x, Y = y, Rotation = rot, Children = children,
                    ImagePath = GetString(element, "image") ?? string.Empty,
                    Width     = GetDouble(element, "width", 0),
                    Height    = GetDouble(element, "height", 0),
                    Center    = GetBool(element, "center", false)
                };

            case "showhide":
                return new ShowHideNode
                {
                    X = x, Y = y, Rotation = rot, Children = children,
                    Input = ParseExpression(GetString(element, "input"), "1")
                };

            case "slider":
                return new SliderNode
                {
                    X = x, Y = y, Rotation = rot, Children = children,
                    InputX = ParseExpression(GetString(element, "inputX"), "0"),
                    InputY = ParseExpression(GetString(element, "inputY"), "0"),
                    InputR = ParseExpression(GetString(element, "inputR"), "0")
                };

            case "pbar":
                return new PBarNode
                {
                    X = x, Y = y, Rotation = rot, Children = children,
                    Input      = ParseExpression(GetString(element, "input"),  "0"),
                    Min        = ParseExpression(GetString(element, "min"),    "0"),
                    Max        = ParseExpression(GetString(element, "max"),    "1"),
                    Direction  = ParseDirection(GetString(element, "direction")),
                    ImagePath  = GetString(element, "image"),
                    Foreground = GetString(element, "foreground") ?? "FFFFFFFF",
                    Background = GetString(element, "background") ?? "00000000",
                    Width      = GetDouble(element, "width", 0),
                    Height     = GetDouble(element, "height", 0),
                    Center     = GetBool(element, "center", false)
                };

            // The remaining VSCView types (ppie, basic3d1) are
            // declared in the spec but not yet rendered by Autofire — we
            // log at Debug and treat them as group nodes so any children
            // still render in the right coordinate space. Adding real
            // implementations is mechanical: declare a node type in
            // ThemeNodes.cs, parse it here, render it in ThemeRenderer.
            case "ppie":
            case "basic3d1":
            default:
                Log.Debug(
                    "Theme element type '{Type}' is not yet rendered; treating as a group.",
                    type);
                return new GroupNode
                {
                    X = x, Y = y, Rotation = rot, Children = children
                };

            case "trailpad":
                return new TrailPadNode
                {
                    X = x, Y = y, Rotation = rot, Children = children,
                    Input = ParseExpression(GetString(element, "input"), "0"),
                    InputX = ParseExpression(GetString(element, "inputX"), "0"),
                    InputY = ParseExpression(GetString(element, "inputY"), "0"),
                    ImagePath = GetString(element, "image") ?? string.Empty,
                    Width = GetDouble(element, "width", 0),
                    Height = GetDouble(element, "height", 0)
                };
        }
    }

    /// <summary>
    /// Compiles a Flee expression string, returning a constant
    /// <see cref="LiteralNode"/> matching <paramref name="defaultExpr"/>
    /// if the source is null or whitespace. Errors are logged and the
    /// expression falls back to 0 so a single bad formula doesn't sink
    /// the whole theme.
    /// </summary>
    private static FleeNode ParseExpression(string? source, string defaultExpr)
    {
        var src = string.IsNullOrWhiteSpace(source) ? defaultExpr : source;
        try
        {
            return FleeParser.Parse(src);
        }
        catch (FleeParseException ex)
        {
            Log.Warning(ex, "Could not parse Flee expression '{Source}'; substituting 0.", src);
            return new LiteralNode(0);
        }
    }

    private static PBarDirection ParseDirection(string? raw) => (raw ?? "right").ToLowerInvariant() switch
    {
        "up"    => PBarDirection.Up,
        "down"  => PBarDirection.Down,
        "left"  => PBarDirection.Left,
        _       => PBarDirection.Right
    };

    // ─── Low-level JSON helpers ───────────────────────────────────────────────

    private static string? GetString(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out var v)) { return null; }
        return v.ValueKind switch
        {
            JsonValueKind.String => v.GetString(),
            JsonValueKind.Null   => null,
            _                    => v.GetRawText()
        };
    }

    private static double GetDouble(JsonElement element, string property, double fallback)
    {
        if (!element.TryGetProperty(property, out var v)) { return fallback; }
        return v.ValueKind switch
        {
            JsonValueKind.Number => v.GetDouble(),
            // String-encoded numbers (e.g. "1534") parse via the same
            // invariant-culture helper the Flee tokenizer uses so locale
            // never changes how a theme.json deserialises.
            JsonValueKind.String when FleeNumber.TryParse(v.GetString()!, out var d) => d,
            _ => fallback
        };
    }

    private static bool GetBool(JsonElement element, string property, bool fallback)
    {
        if (!element.TryGetProperty(property, out var v)) { return fallback; }
        return v.ValueKind switch
        {
            JsonValueKind.True   => true,
            JsonValueKind.False  => false,
            JsonValueKind.Number => v.GetDouble() != 0,
            JsonValueKind.String =>
                string.Equals(v.GetString(), "true", StringComparison.OrdinalIgnoreCase),
            _ => fallback
        };
    }
}

/// <summary>Thrown when theme JSON is malformed or references unsupported schema.</summary>
public sealed class ThemeLoadException(string message, Exception? inner = null)
    : Exception(message, inner);
