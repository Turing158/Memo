using Avalonia;
using Avalonia.Media;
using Avalonia.Styling;
using Memo.Models;
using System;
using System.Collections.Generic;

namespace Memo.UI;

internal static class ThemePreferences {
    private static readonly IReadOnlyDictionary<string, Color> LightPalette = CreatePalette(
        bgPrimary: "#F6F3EC",
        bgSecondary: "#FAF8F3",
        bgTertiary: "#EFEAE1",
        bgHover: "#EBE5DA",
        surfacePrimary: "#FFFFFF",
        surfaceHover: "#FBF9F4",
        surfaceActive: "#F2EDE4",
        borderDefault: "#E5DDD1",
        borderHover: "#D6CDBE",
        borderSubtle: "#EDE8DD",
        borderEmphasis: "#CDBBA8",
        borderFocus: "#C97B5A",
        accentPrimary: "#C06A48",
        accentHover: "#A8583A",
        accentMuted: "#E8C4A8",
        accentSubtle: "#FAEDE4",
        accentSubtlePressed: "#DDAE8E",
        accentPressed: "#964E32",
        dangerPrimary: "#C5543D",
        dangerHover: "#A8432E",
        dangerSubtle: "#FBE8E3",
        successPrimary: "#4E8B6F",
        successHover: "#3F765D",
        successPressed: "#315F4A",
        successSubtle: "#E4F0EA",
        textPrimary: "#1E1A16",
        textSecondary: "#5C554D",
        textTertiary: "#8A8278",
        textDisabled: "#B0A79B",
        iconDefault: "#6B6359",
        iconHover: "#1E1A16",
        iconAccent: "#C06A48",
        textOnAccent: "#FFFFFF");

    private static readonly IReadOnlyDictionary<string, Color> DarkPalette = CreatePalette(
        bgPrimary: "#181917",
        bgSecondary: "#1D1F1C",
        bgTertiary: "#282A26",
        bgHover: "#2D302B",
        surfacePrimary: "#222420",
        surfaceHover: "#282B26",
        surfaceActive: "#31342E",
        borderDefault: "#3B3F38",
        borderHover: "#4B5147",
        borderSubtle: "#30342E",
        borderEmphasis: "#596052",
        borderFocus: "#D69A78",
        accentPrimary: "#D58B68",
        accentHover: "#E1A080",
        accentMuted: "#98664F",
        accentSubtle: "#3A2B24",
        accentSubtlePressed: "#573C30",
        accentPressed: "#B87355",
        dangerPrimary: "#D87361",
        dangerHover: "#E58A79",
        dangerSubtle: "#3D2926",
        successPrimary: "#72AD8F",
        successHover: "#86C2A2",
        successPressed: "#579174",
        successSubtle: "#22352C",
        textPrimary: "#F0ECE3",
        textSecondary: "#C8C1B6",
        textTertiary: "#A49D92",
        textDisabled: "#746F67",
        iconDefault: "#BAB2A6",
        iconHover: "#F0ECE3",
        iconAccent: "#D58B68",
        textOnAccent: "#211914");

    private static Application? _application;

    public static ThemeMode Mode { get; private set; } = ThemeMode.FollowSystem;

    public static void Initialize(Application application) {
        if (_application != null)
            _application.ActualThemeVariantChanged -= OnActualThemeVariantChanged;

        _application = application;
        _application.ActualThemeVariantChanged += OnActualThemeVariantChanged;
        ApplyMode(Mode);
    }

    public static void ApplyMode(ThemeMode mode) {
        if (!Enum.IsDefined(mode)) mode = ThemeMode.FollowSystem;
        Mode = mode;
        if (_application == null) return;

        _application.RequestedThemeVariant = mode switch {
            ThemeMode.Light => ThemeVariant.Light,
            ThemeMode.Dark => ThemeVariant.Dark,
            _ => ThemeVariant.Default,
        };

        ApplyPalette(mode == ThemeMode.Dark
            || (mode == ThemeMode.FollowSystem && _application.ActualThemeVariant == ThemeVariant.Dark));
    }

    public static void Shutdown() {
        if (_application != null)
            _application.ActualThemeVariantChanged -= OnActualThemeVariantChanged;
        _application = null;
    }

    private static void OnActualThemeVariantChanged(object? sender, EventArgs e) {
        if (Mode == ThemeMode.FollowSystem)
            ApplyPalette(_application?.ActualThemeVariant == ThemeVariant.Dark);
    }

    private static void ApplyPalette(bool useDarkPalette) {
        if (_application == null) return;
        var palette = useDarkPalette ? DarkPalette : LightPalette;
        foreach (var (key, color) in palette) {
            if (_application.Resources[key] is SolidColorBrush brush)
                brush.Color = color;
        }
    }

    private static IReadOnlyDictionary<string, Color> CreatePalette(
        string bgPrimary,
        string bgSecondary,
        string bgTertiary,
        string bgHover,
        string surfacePrimary,
        string surfaceHover,
        string surfaceActive,
        string borderDefault,
        string borderHover,
        string borderSubtle,
        string borderEmphasis,
        string borderFocus,
        string accentPrimary,
        string accentHover,
        string accentMuted,
        string accentSubtle,
        string accentSubtlePressed,
        string accentPressed,
        string dangerPrimary,
        string dangerHover,
        string dangerSubtle,
        string successPrimary,
        string successHover,
        string successPressed,
        string successSubtle,
        string textPrimary,
        string textSecondary,
        string textTertiary,
        string textDisabled,
        string iconDefault,
        string iconHover,
        string iconAccent,
        string textOnAccent) => new Dictionary<string, Color> {
            ["BgPrimaryBrush"] = Color.Parse(bgPrimary),
            ["BgSecondaryBrush"] = Color.Parse(bgSecondary),
            ["BgTertiaryBrush"] = Color.Parse(bgTertiary),
            ["BgHoverBrush"] = Color.Parse(bgHover),
            ["SurfacePrimaryBrush"] = Color.Parse(surfacePrimary),
            ["SurfaceHoverBrush"] = Color.Parse(surfaceHover),
            ["SurfaceActiveBrush"] = Color.Parse(surfaceActive),
            ["BorderDefaultBrush"] = Color.Parse(borderDefault),
            ["BorderHoverBrush"] = Color.Parse(borderHover),
            ["BorderSubtleBrush"] = Color.Parse(borderSubtle),
            ["BorderEmphasisBrush"] = Color.Parse(borderEmphasis),
            ["BorderFocusBrush"] = Color.Parse(borderFocus),
            ["AccentPrimaryBrush"] = Color.Parse(accentPrimary),
            ["AccentHoverBrush"] = Color.Parse(accentHover),
            ["AccentMutedBrush"] = Color.Parse(accentMuted),
            ["AccentSubtleBrush"] = Color.Parse(accentSubtle),
            ["AccentSubtlePressedBrush"] = Color.Parse(accentSubtlePressed),
            ["AccentPressedBrush"] = Color.Parse(accentPressed),
            ["DangerPrimaryBrush"] = Color.Parse(dangerPrimary),
            ["DangerHoverBrush"] = Color.Parse(dangerHover),
            ["DangerSubtleBrush"] = Color.Parse(dangerSubtle),
            ["SuccessPrimaryBrush"] = Color.Parse(successPrimary),
            ["SuccessHoverBrush"] = Color.Parse(successHover),
            ["SuccessPressedBrush"] = Color.Parse(successPressed),
            ["SuccessSubtleBrush"] = Color.Parse(successSubtle),
            ["TextPrimaryBrush"] = Color.Parse(textPrimary),
            ["TextSecondaryBrush"] = Color.Parse(textSecondary),
            ["TextTertiaryBrush"] = Color.Parse(textTertiary),
            ["TextDisabledBrush"] = Color.Parse(textDisabled),
            ["IconDefaultBrush"] = Color.Parse(iconDefault),
            ["IconHoverBrush"] = Color.Parse(iconHover),
            ["IconAccentBrush"] = Color.Parse(iconAccent),
            ["TextOnAccentBrush"] = Color.Parse(textOnAccent),
        };
}
