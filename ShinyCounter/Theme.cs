using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace ShinyCounter;

public sealed class Theme
{
    public string Name { get; set; } = "";
    public string Decor { get; set; } = ""; // "" or "pokeballs"
    public Dictionary<string, string> Colors { get; set; } = new();
}

/// Themes define 12 base colors; hover/dim/border variants are derived
/// automatically so community theme files stay simple.
public static class ThemeManager
{
    public static readonly string ThemesDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ShinyCounter", "Themes");

    public static Color Accent { get; private set; } = (Color)ColorConverter.ConvertFromString("#A78BFA");
    public static Color Text { get; private set; } = (Color)ColorConverter.ConvertFromString("#F4F4F5");
    public static bool DarkTitleBar { get; private set; } = true;
    public static string CurrentDecor { get; private set; } = "";

    private static readonly Dictionary<string, string> Dark = new()
    {
        ["bg"] = "#0E0E10", ["surface"] = "#18181B", ["surface2"] = "#1F1F23",
        ["border"] = "#14FFFFFF", ["borderHover"] = "#26FFFFFF",
        ["text"] = "#F4F4F5", ["muted"] = "#71717A", ["hint"] = "#3F3F46",
        ["accent"] = "#A78BFA", ["danger"] = "#F87171", ["success"] = "#34D399", ["warning"] = "#FBBF24",
    };

    private static readonly Dictionary<string, string> Light = new()
    {
        ["bg"] = "#ECECEE", ["surface"] = "#FFFFFF", ["surface2"] = "#F4F4F5",
        ["border"] = "#14000000", ["borderHover"] = "#26000000",
        ["text"] = "#18181B", ["muted"] = "#6B6B74", ["hint"] = "#D4D4D8",
        ["accent"] = "#7C3AED", ["danger"] = "#DC2626", ["success"] = "#059669", ["warning"] = "#B45309",
    };

    private static readonly Dictionary<string, string> Pokemon = new()
    {
        ["bg"] = "#0A1A2F", ["surface"] = "#132845", ["surface2"] = "#1B365C",
        ["border"] = "#24FFFFFF", ["borderHover"] = "#3BFFFFFF",
        ["text"] = "#F8FAFF", ["muted"] = "#8FA3C2", ["hint"] = "#2E4A73",
        ["accent"] = "#FFCB05", ["danger"] = "#EE1515", ["success"] = "#43C06E", ["warning"] = "#FF9C33",
    };

    /// Write the stock theme files so the community has live examples to copy.
    /// Stock files are refreshed every launch — copy and rename one to customize it.
    public static void EnsureStockThemes()
    {
        try
        {
            Directory.CreateDirectory(ThemesDir);
            WriteStock("Dark", Dark, "");
            WriteStock("Light", Light, "");
            WriteStock("Pokemon", Pokemon, "pokeballs");
        }
        catch { }
    }

    private static void WriteStock(string name, Dictionary<string, string> colors, string decor)
    {
        File.WriteAllText(Path.Combine(ThemesDir, name + ".json"), JsonSerializer.Serialize(
            new Theme { Name = name, Decor = decor, Colors = colors },
            new JsonSerializerOptions { WriteIndented = true }));
    }

    public static List<Theme> LoadAll()
    {
        var themes = new List<Theme>();
        try
        {
            foreach (string file in Directory.GetFiles(ThemesDir, "*.json"))
            {
                try
                {
                    var t = JsonSerializer.Deserialize<Theme>(File.ReadAllText(file),
                        new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                    if (t is null || t.Colors.Count == 0) continue;
                    if (string.IsNullOrWhiteSpace(t.Name)) t.Name = Path.GetFileNameWithoutExtension(file);
                    themes.Add(t);
                }
                catch { /* skip malformed community files */ }
            }
        }
        catch { }

        if (themes.Count == 0) themes.Add(new Theme { Name = "Dark", Colors = Dark });

        // Stock first, community themes after, both alphabetical within their group
        string[] stockOrder = { "Dark", "Light", "Pokemon" };
        return themes
            .OrderBy(t => { int i = Array.IndexOf(stockOrder, t.Name); return i < 0 ? int.MaxValue : i; })
            .ThenBy(t => t.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public static void Apply(string name)
    {
        var theme = LoadAll().FirstOrDefault(t => t.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                    ?? new Theme { Name = "Dark", Colors = Dark };

        Color C(string key)
        {
            string hex = theme.Colors.TryGetValue(key, out var v) ? v : Dark[key];
            try { return (Color)ColorConverter.ConvertFromString(hex); }
            catch { return (Color)ColorConverter.ConvertFromString(Dark[key]); }
        }

        Color bg = C("bg"), accent = C("accent"), danger = C("danger"),
              success = C("success"), warning = C("warning");
        Accent = accent;
        Text = C("text");
        CurrentDecor = theme.Decor ?? "";

        SetBrush("BgBrush", bg);
        SetBrush("SurfaceBrush", C("surface"));
        SetBrush("Surface2Brush", C("surface2"));
        SetBrush("BorderBrush2", C("border"));
        SetBrush("BorderHoverBrush", C("borderHover"));
        SetBrush("TextBrush", Text);
        SetBrush("MutedBrush", C("muted"));
        SetBrush("HintBrush", C("hint"));

        SetBrush("AccentBrush", accent);
        SetBrush("AccentDimBrush", WithAlpha(accent, 0x1F));
        SetBrush("AccentHoverBrush", WithAlpha(accent, 0x33));
        SetBrush("AccentBorderBrush", WithAlpha(accent, 0x4D));

        SetBrush("DangerBrush", danger);
        SetBrush("DangerDimBrush", WithAlpha(danger, 0x1A));
        SetBrush("DangerHoverBrush", WithAlpha(danger, 0x2E));
        SetBrush("DangerBorderBrush", WithAlpha(danger, 0x40));

        SetBrush("SuccessBrush", success);
        SetBrush("SuccessDimBrush", WithAlpha(success, 0x1A));
        SetBrush("SuccessHoverBrush", WithAlpha(success, 0x2E));
        SetBrush("SuccessBorderBrush", WithAlpha(success, 0x40));

        SetBrush("WarningBrush", warning);
        SetBrush("WarningDimBrush", WithAlpha(warning, 0x1A));

        DarkTitleBar = Luminance(bg) < 0.5;
        foreach (Window w in Application.Current.Windows) ApplyTitleBar(w);
    }

    /// Replace the app-level resource — every DynamicResource reference updates live.
    private static void SetBrush(string key, Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        Application.Current.Resources[key] = brush;
    }

    private static Color WithAlpha(Color c, byte a) => Color.FromArgb(a, c.R, c.G, c.B);

    private static double Luminance(Color c) => (0.299 * c.R + 0.587 * c.G + 0.114 * c.B) / 255;

    public static void ApplyTitleBar(Window window)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero) return;
        int dark = DarkTitleBar ? 1 : 0;
        DwmSetWindowAttribute(handle, 20, ref dark, sizeof(int));
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
}
