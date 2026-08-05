using Avalonia;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;
using GameFlow.App.ViewModels;

namespace GameFlow.App.Services;

/// <summary>
/// Applies color-palette themes at runtime by replacing entries in
/// the application's merged resource dictionaries and toggling
/// Avalonia's RequestedThemeVariant for Dark / Light base modes.
/// </summary>
public static class AppThemeService
{
    private static readonly IReadOnlyDictionary<string, string> CyberBlueAccents = new Dictionary<string, string>
    {
        ["AppAccent"]       = "#00D4FF",
        ["AppAccentSoft"]   = "#0F2530",
        ["AppAccentHover"]  = "#22DEFF",
        ["AppBorderActive"] = "#00D4FF",
        ["AppSurface0"]     = "#050B12",
        ["AppSurface1"]     = "#07101D",
        ["AppSurface2"]     = "#0A1628",
        ["AppBorder0"]      = "#0F2035",
        ["AppBorder1"]      = "#1E3A5A",
        ["AppForeground"]   = "#E2E8F0",
        ["AppForegroundDim"]= "#7D8BA3",
        ["AppPrimaryBtn"]   = "#040C16"
    };

    private static readonly IReadOnlyDictionary<string, string> MidnightPurpleAccents = new Dictionary<string, string>
    {
        ["AppAccent"]       = "#A78BFA",
        ["AppAccentSoft"]   = "#1E1035",
        ["AppAccentHover"]  = "#BBA5FF",
        ["AppBorderActive"] = "#A78BFA",
        ["AppSurface0"]     = "#0A0812",
        ["AppSurface1"]     = "#100D1C",
        ["AppSurface2"]     = "#16112A",
        ["AppBorder0"]      = "#221840",
        ["AppBorder1"]      = "#3B2870",
        ["AppForeground"]   = "#EDE9F6",
        ["AppForegroundDim"]= "#887FA3",
        ["AppPrimaryBtn"]   = "#0D0820"
    };

    private static readonly IReadOnlyDictionary<string, string> NeonGreenAccents = new Dictionary<string, string>
    {
        ["AppAccent"]       = "#22C55E",
        ["AppAccentSoft"]   = "#0A2818",
        ["AppAccentHover"]  = "#4ADE80",
        ["AppBorderActive"] = "#22C55E",
        ["AppSurface0"]     = "#060C08",
        ["AppSurface1"]     = "#091410",
        ["AppSurface2"]     = "#0C1C14",
        ["AppBorder0"]      = "#112B1A",
        ["AppBorder1"]      = "#1A4B2A",
        ["AppForeground"]   = "#E0F2E9",
        ["AppForegroundDim"]= "#6F8D79",
        ["AppPrimaryBtn"]   = "#040E06"
    };

    private static readonly IReadOnlyDictionary<string, string> SolarRedAccents = new Dictionary<string, string>
    {
        ["AppAccent"]       = "#F97316",
        ["AppAccentSoft"]   = "#2C1206",
        ["AppAccentHover"]  = "#FB923C",
        ["AppBorderActive"] = "#F97316",
        ["AppSurface0"]     = "#0F0802",
        ["AppSurface1"]     = "#1A1005",
        ["AppSurface2"]     = "#221607",
        ["AppBorder0"]      = "#31200A",
        ["AppBorder1"]      = "#5A3A10",
        ["AppForeground"]   = "#FEF3E2",
        ["AppForegroundDim"]= "#A77E58",
        ["AppPrimaryBtn"]   = "#0E0602"
    };

    private static readonly IReadOnlyDictionary<string, string> LightAccents = new Dictionary<string, string>
    {
        ["AppAccent"]       = "#0077CC",
        ["AppAccentSoft"]   = "#EBF5FF",
        ["AppAccentHover"]  = "#005FAA",
        ["AppBorderActive"] = "#0077CC",
        ["AppSurface0"]     = "#FFFFFF",
        ["AppSurface1"]     = "#F0F4F8",
        ["AppSurface2"]     = "#E2EAF0",
        ["AppBorder0"]      = "#CBD5E0",
        ["AppBorder1"]      = "#94A3B8",
        ["AppForeground"]   = "#0F172A",
        ["AppForegroundDim"]= "#475569",
        ["AppPrimaryBtn"]   = "#FFFFFF"
    };

    /// <summary>
    /// Applies the supplied theme to the running Avalonia app: toggles the
    /// requested theme variant (Dark / Light) and overwrites the accent
    /// colour entries in the merged resource dictionary.
    /// </summary>
    /// <param name="kind">The theme to apply. Unknown values fall back to
    /// <see cref="AppThemeKind.CyberBlue"/>.</param>
    public static void Apply(AppThemeKind kind)
    {
        if (Application.Current is null)
        {
            Serilog.Log.Debug("AppThemeService.Apply called with no active Application — ignoring.");
            return;
        }

        var palette = kind switch
        {
            AppThemeKind.MidnightPurple => MidnightPurpleAccents,
            AppThemeKind.NeonGreen => NeonGreenAccents,
            AppThemeKind.SolarRed => SolarRedAccents,
            AppThemeKind.Light => LightAccents,
            _ => CyberBlueAccents
        };

        // Toggle Avalonia's RequestedThemeVariant so FluentTheme adapts its base controls
        Application.Current.RequestedThemeVariant = kind == AppThemeKind.Light
            ? ThemeVariant.Light
            : ThemeVariant.Dark;

        // Inject our accent colors into the application resource dictionary
        var applied = 0;
        var skipped = 0;
        foreach (var (key, hex) in palette)
        {
            if (Color.TryParse(hex, out var color))
            {
                Application.Current.Resources[key] = new SolidColorBrush(color);
                applied++;
            }
            else
            {
                // A bad hex string in the palette table is a developer error,
                // not a runtime one — surface it at Debug so it shows up in
                // verbose logs but doesn't pollute Information.
                Serilog.Log.Debug(
                    "Skipping unparseable colour {ColourValue} for resource key {ResourceKey} in theme {Theme}.",
                    hex,
                    key,
                    kind);
                skipped++;
            }
        }

        var isLight = kind == AppThemeKind.Light;
        var semanticApplied = ApplySemanticPalette(Application.Current, isLight);
        var systemApplied = ApplySystemPalette(Application.Current, palette, isLight);

        Serilog.Log.Information(
            "Applied theme {Theme} ({Applied}/{Total} app colours, {SemanticApplied} semantic colours, {SystemApplied} system colours; {Skipped} skipped).",
            kind,
            applied,
            palette.Count,
            semanticApplied,
            systemApplied,
            skipped);
    }

    /// <summary>
    /// Applies semantic status colors independently from the selected brand
    /// accent. Dark and light variants use different foreground strengths and
    /// soft fills so small labels and destructive actions remain readable.
    /// </summary>
    private static int ApplySemanticPalette(Application app, bool isLight)
    {
        var colors = isLight
            ? new Dictionary<string, string>
            {
                ["AppSuccess"] = "#047857",
                ["AppSuccessSoft"] = "#D1FAE5",
                ["AppWarning"] = "#92400E",
                ["AppWarningSoft"] = "#FEF3C7",
                ["AppDanger"] = "#B91C1C",
                ["AppDangerSoft"] = "#FEE2E2",
                ["AppInfo"] = "#1D4ED8",
                ["AppInfoSoft"] = "#DBEAFE",
                ["AppFeature"] = "#6D28D9",
                ["AppFeatureSoft"] = "#EDE9FE"
            }
            : new Dictionary<string, string>
            {
                ["AppSuccess"] = "#34D399",
                ["AppSuccessSoft"] = "#0C2A21",
                ["AppWarning"] = "#FBBF24",
                ["AppWarningSoft"] = "#33250A",
                ["AppDanger"] = "#FB7185",
                ["AppDangerSoft"] = "#35121A",
                ["AppInfo"] = "#60A5FA",
                ["AppInfoSoft"] = "#102440",
                ["AppFeature"] = "#C4B5FD",
                ["AppFeatureSoft"] = "#25193D"
            };

        foreach (var (key, hex) in colors)
        {
            if (Color.TryParse(hex, out var color))
            {
                app.Resources[key] = new SolidColorBrush(color);
            }
        }

        return colors.Count;
    }

    /// <summary>
    /// Mirrors a GameFlow palette into Avalonia's built-in
    /// <c>System*Color</c> resources.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Fluent derives every stock control's accent, selection, hover,
    /// border and disabled colour from this WinRT-style colour palette,
    /// and — as of Avalonia 12 — those keys are the only theme entries
    /// its control templates still resolve through
    /// <c>DynamicResource</c>. Everything else is baked in at XAML
    /// compile time and cannot be overridden from an application
    /// dictionary at all.
    /// </para>
    /// <para>
    /// Writing them here is therefore what makes ComboBox popups, scroll
    /// bars, check boxes, radio buttons, sliders, progress bars, tool
    /// tips and context menus follow the selected GameFlow theme rather
    /// than staying on Fluent's stock blue-on-grey. The alternative —
    /// re-templating each control in <c>AppTheme.axaml</c> — is what the
    /// app used to attempt, and several of those rules silently matched
    /// nothing because they named template parts Fluent no longer has.
    /// </para>
    /// </remarks>
    /// <param name="app">The running application whose resources receive the palette.</param>
    /// <param name="palette">The GameFlow palette to derive system colours from.</param>
    /// <param name="isLight"><see langword="true"/> for the light base variant.</param>
    /// <returns>The number of system colour entries written.</returns>
    private static int ApplySystemPalette(
        Application app,
        IReadOnlyDictionary<string, string> palette,
        bool isLight)
    {
        var accent = Resolve(palette, "AppAccent", Color.FromRgb(0x00, 0xD4, 0xFF));
        var surface0 = Resolve(palette, "AppSurface0", Color.FromRgb(0x05, 0x0B, 0x12));
        var surface1 = Resolve(palette, "AppSurface1", Color.FromRgb(0x07, 0x10, 0x1D));
        var surface2 = Resolve(palette, "AppSurface2", Color.FromRgb(0x0A, 0x16, 0x28));
        var border0 = Resolve(palette, "AppBorder0", Color.FromRgb(0x0F, 0x20, 0x35));
        var border1 = Resolve(palette, "AppBorder1", Color.FromRgb(0x1E, 0x3A, 0x5A));
        var foreground = Resolve(palette, "AppForeground", Color.FromRgb(0xE2, 0xE8, 0xF0));
        var foregroundDim = Resolve(palette, "AppForegroundDim", Color.FromRgb(0x64, 0x74, 0x8B));

        // "Toward the edge of the palette": on a dark theme every step
        // brightens, on a light theme every step darkens. Deriving the
        // ramps rather than hand-authoring 20 extra hex values per theme
        // keeps a new palette a 12-line addition, as it is today.
        var edge = isLight ? Colors.Black : Colors.White;

        var systemColors = new Dictionary<string, Color>
        {
            // Accent ramp — check marks, radio dots, slider fills,
            // progress indicators, selection and focus visuals.
            ["SystemAccentColor"] = accent,
            ["SystemAccentColorLight1"] = Blend(accent, Colors.White, 0.20),
            ["SystemAccentColorLight2"] = Blend(accent, Colors.White, 0.38),
            ["SystemAccentColorLight3"] = Blend(accent, Colors.White, 0.56),
            ["SystemAccentColorDark1"] = Blend(accent, Colors.Black, 0.20),
            ["SystemAccentColorDark2"] = Blend(accent, Colors.Black, 0.38),
            ["SystemAccentColorDark3"] = Blend(accent, Colors.Black, 0.56),

            // Base ramp — control foregrounds, from primary text down to
            // the disabled/placeholder end.
            ["SystemBaseHighColor"] = foreground,
            ["SystemBaseMediumHighColor"] = Blend(foreground, foregroundDim, 0.35),
            ["SystemBaseMediumColor"] = foregroundDim,
            ["SystemBaseMediumLowColor"] = Blend(foregroundDim, surface0, 0.35),
            ["SystemBaseLowColor"] = border1,

            // Alt ramp — control fills layered over the page.
            ["SystemAltHighColor"] = surface0,
            ["SystemAltMediumHighColor"] = surface1,
            ["SystemAltMediumColor"] = surface1,
            ["SystemAltMediumLowColor"] = surface2,
            ["SystemAltLowColor"] = surface2,

            // Chrome ramp — flyout, popup and menu surfaces. A ComboBox
            // drop-down reads SystemChromeMediumLowColor, so getting this
            // wrong is what leaves popups looking bolted on from another
            // application.
            ["SystemChromeHighColor"] = border1,
            ["SystemChromeMediumColor"] = surface2,
            ["SystemChromeMediumLowColor"] = surface1,
            ["SystemChromeLowColor"] = surface1,
            ["SystemChromeAltLowColor"] = surface2,
            ["SystemChromeWhiteColor"] = isLight ? Colors.White : foreground,
            ["SystemChromeBlackHighColor"] = surface0,
            ["SystemChromeBlackMediumColor"] = surface0,
            ["SystemChromeBlackMediumLowColor"] = surface1,
            ["SystemChromeBlackLowColor"] = surface1,
            ["SystemChromeGrayColor"] = foregroundDim,
            ["SystemChromeDisabledHighColor"] = border0,
            ["SystemChromeDisabledLowColor"] = Blend(foregroundDim, surface0, 0.45),

            // List interaction states — ComboBoxItem/MenuItem hover and
            // press, and the highlight behind a selected row.
            ["SystemListLowColor"] = Blend(surface2, edge, 0.06),
            ["SystemListMediumColor"] = Blend(surface2, edge, 0.12),
            ["SystemRevealListLowColor"] = Blend(surface2, edge, 0.06),
            ["SystemRevealListMediumColor"] = Blend(surface2, edge, 0.12),

            ["SystemRegionColor"] = surface0,
            ["SystemErrorTextColor"] = isLight
                ? Color.FromRgb(0xB9, 0x1C, 0x1C)
                : Color.FromRgb(0xFB, 0x71, 0x85)
        };

        foreach (var (key, color) in systemColors)
        {
            app.Resources[key] = color;
        }

        // The three WinUI-named brushes Fluent 12 still resolves
        // dynamically. They sit outside the System*Color palette and
        // carry the surfaces the palette cannot reach: a drop-down or
        // flyout paints its background from SolidBackgroundFillColorBase,
        // which otherwise stays a neutral grey and makes every ComboBox
        // popup look borrowed from another application.
        var subtleFills = new Dictionary<string, Color>
        {
            ["SolidBackgroundFillColorBaseBrush"] = surface2,
            ["SubtleFillColorSecondaryBrush"] = Blend(surface2, edge, 0.07),
            ["SubtleFillColorTertiaryBrush"] = Blend(surface2, edge, 0.13)
        };

        foreach (var (key, color) in subtleFills)
        {
            app.Resources[key] = new SolidColorBrush(color);
        }

        return systemColors.Count + subtleFills.Count;
    }

    /// <summary>
    /// Reads a colour out of a palette table, falling back to
    /// <paramref name="fallback"/> when the key is absent or its hex
    /// string does not parse.
    /// </summary>
    private static Color Resolve(IReadOnlyDictionary<string, string> palette, string key, Color fallback) =>
        palette.TryGetValue(key, out var hex) && Color.TryParse(hex, out var color) ? color : fallback;

    /// <summary>
    /// Linearly interpolates between two colours in sRGB space.
    /// </summary>
    /// <param name="from">The colour returned at <paramref name="amount"/> 0.</param>
    /// <param name="to">The colour approached as <paramref name="amount"/> reaches 1.</param>
    /// <param name="amount">Blend factor, clamped to 0‥1.</param>
    private static Color Blend(Color from, Color to, double amount)
    {
        var t = Math.Clamp(amount, 0d, 1d);
        return Color.FromArgb(
            byte.MaxValue,
            (byte)Math.Round(from.R + ((to.R - from.R) * t)),
            (byte)Math.Round(from.G + ((to.G - from.G) * t)),
            (byte)Math.Round(from.B + ((to.B - from.B) * t)));
    }
}
