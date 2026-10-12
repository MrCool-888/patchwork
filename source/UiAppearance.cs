using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Interop;
using System.Windows.Media;

namespace Patchwork
{
    internal sealed class UiAppearance
    {
        internal const string DefaultTheme = "Dark", DefaultAccent = "Violet";
        internal static readonly string[] Themes = { "Dark", "Light" };
        internal static readonly string[] Accents = { "Violet", "Blue", "Mint", "Amber" };
        readonly Dictionary<string, SolidColorBrush> brushes = new Dictionary<string, SolidColorBrush>(StringComparer.Ordinal);
        readonly Dictionary<string, PaletteColor> colors = new Dictionary<string, PaletteColor>(StringComparer.Ordinal);
        string theme = DefaultTheme, accent = DefaultAccent;

        internal UiAppearance(ResourceDictionary resources)
        {
            if (resources == null) throw new ArgumentNullException("resources");
            foreach (string key in Keys)
            {
                colors[key] = new PaletteColor(); brushes[key] = new SolidColorBrush();
                // A binding keeps WPF styles from freezing brushes needed for live theme changes.
                BindingOperations.SetBinding(brushes[key], SolidColorBrush.ColorProperty, new Binding("Value") { Source = colors[key] });
                resources[key] = brushes[key];
            }
            Apply(theme, accent);
        }

        internal string Theme { get { return theme; } }
        internal string Accent { get { return accent; } }
        internal Brush Brush(string legacyHex)
        {
            string key;
            if (Legacy.TryGetValue((legacyHex ?? "").ToUpperInvariant(), out key)) return brushes[key];
            try { return (Brush)new BrushConverter().ConvertFromString(legacyHex); }
            catch { return brushes["UiText"]; }
        }
        internal SolidColorBrush Semantic(string key) { return brushes[key]; }

        internal void Apply(string newTheme, string newAccent)
        {
            theme = Array.IndexOf(Themes, newTheme) >= 0 ? newTheme : DefaultTheme;
            accent = Array.IndexOf(Accents, newAccent) >= 0 ? newAccent : DefaultAccent;
            bool dark = theme == "Dark";
            string[] accentColors = accent == "Blue" ? new[] { "#5AA7F5", "#10243A", dark ? "#B9DCFF" : "#154F88", dark ? "#183047" : "#DFEDFC", "#1D4269" }
                : accent == "Mint" ? new[] { "#39A987", "#102C27", dark ? "#A4E9D4" : "#0F6048", dark ? "#18362F" : "#DDF3EC", "#1B463B" }
                : accent == "Amber" ? new[] { "#D99A32", "#33240F", dark ? "#FFE0A3" : "#704300", dark ? "#3B2E1D" : "#FFF0D3", "#60400F" }
                : new[] { "#A18BCF", "#241B38", dark ? "#D8C9FF" : "#4D376F", dark ? "#2A223E" : "#E9E1FB", "#49366D" };
            var values = new Dictionary<string, string>
            {
                { "UiBackground", dark ? "#0B1019" : "#F3F5F9" }, { "UiSidebar", dark ? "#101722" : "#E8ECF3" },
                { "UiSurface", dark ? "#151D2B" : "#FFFFFF" }, { "UiSurfaceAlt", dark ? "#1B2332" : "#E9EDF4" },
                { "UiInput", dark ? "#101722" : "#FFFFFF" }, { "UiBorder", dark ? "#596981" : "#7F8EA3" },
                { "UiText", dark ? "#E8EDF6" : "#202938" }, { "UiMuted", dark ? "#B1BDCF" : "#4E5B6E" },
                { "UiSubtle", dark ? "#A3AEC2" : "#4E5B6E" }, { "UiAccent", accentColors[0] },
                { "UiAccentForeground", accentColors[1] }, { "UiAccentSoft", accentColors[3] },
                { "UiAccentText", accentColors[2] }, { "UiHover", dark ? "#283247" : "#DCE3ED" },
                { "UiSelection", accentColors[4] }, { "UiSuccess", dark ? "#85DCC0" : "#006B4D" },
                { "UiSuccessSoft", dark ? "#18322E" : "#D7F1E7" }, { "UiWarning", dark ? "#E9C985" : "#835300" },
                { "UiWarningSoft", dark ? "#322A1C" : "#FFF0CC" }, { "UiError", dark ? "#F0BB9B" : "#A33B26" },
                { "UiErrorSoft", dark ? "#3A241F" : "#FBE3DD" }
            };
            foreach (var pair in values) colors[pair.Key].Value = (Color)ColorConverter.ConvertFromString(pair.Value);
        }

        internal static double Contrast(string first, string second)
        {
            double a = Luminance((Color)ColorConverter.ConvertFromString(first));
            double b = Luminance((Color)ColorConverter.ConvertFromString(second));
            return (Math.Max(a, b) + .05) / (Math.Min(a, b) + .05);
        }
        internal static string[] LoadPreferences(string dataRoot)
        {
            try
            {
                string path = Path.Combine(dataRoot, "appearance.json");
                if (!File.Exists(path)) return new[] { DefaultTheme, DefaultAccent };
                var data = Json.Parse(PatchEngine.Decode(PatchEngine.Read(path, 4096)));
                string theme = Json.String(data, "theme", DefaultTheme), accent = Json.String(data, "accent", DefaultAccent);
                return new[] { Array.IndexOf(Themes, theme) >= 0 ? theme : DefaultTheme, Array.IndexOf(Accents, accent) >= 0 ? accent : DefaultAccent };
            }
            catch { return new[] { DefaultTheme, DefaultAccent }; }
        }
        internal static void SavePreferences(string dataRoot, string theme, string accent)
        {
            Directory.CreateDirectory(dataRoot);
            File.WriteAllText(Path.Combine(dataRoot, "appearance.json"), Json.Pretty(new Dictionary<string, object>
            {
                { "theme", Array.IndexOf(Themes, theme) >= 0 ? theme : DefaultTheme },
                { "accent", Array.IndexOf(Accents, accent) >= 0 ? accent : DefaultAccent }
            }), new UTF8Encoding(false));
        }
        static double Luminance(Color color)
        {
            Func<byte, double> linear = c => { double x = c / 255.0; return x <= .04045 ? x / 12.92 : Math.Pow((x + .055) / 1.055, 2.4); };
            return .2126 * linear(color.R) + .7152 * linear(color.G) + .0722 * linear(color.B);
        }

        static readonly string[] Keys = { "UiBackground", "UiSidebar", "UiSurface", "UiSurfaceAlt", "UiInput", "UiBorder", "UiText", "UiMuted", "UiSubtle", "UiAccent", "UiAccentForeground", "UiAccentSoft", "UiAccentText", "UiHover", "UiSelection", "UiSuccess", "UiSuccessSoft", "UiWarning", "UiWarningSoft", "UiError", "UiErrorSoft" };
        static readonly Dictionary<string, string> Legacy = BuildLegacy();
        static Dictionary<string, string> BuildLegacy()
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            Map(result, "UiBackground", "#0B1019"); Map(result, "UiSidebar", "#141C29", "#101620", "#101722", "#111722", "#111824", "#111925", "#121A27", "#131B29", "#131E27");
            Map(result, "UiSurface", "#151D2B", "#161225", "#171125", "#192131"); Map(result, "UiSurfaceAlt", "#1B2332", "#182234", "#20283A", "#202A3B", "#252E43", "#253044", "#253047", "#253149", "#272239");
            Map(result, "UiInput", "#101722", "#101620"); Map(result, "UiBorder", "#283247", "#2B3549", "#2B354A", "#2B364C", "#2C3649", "#303A50", "#313B53", "#364159", "#3B4660", "#43516B", "#46516B", "#56657E", "#65738C", "#66758E", "#67738E", "#73819A", "#8E9BB3");
            Map(result, "UiText", "#E5E9F3", "#E8EDF6", "#EBEFF7", "#EDF1FA"); Map(result, "UiMuted", "#A3AEC2", "#A4C4B9"); Map(result, "UiSubtle", "#78869E", "#929DB1");
            Map(result, "UiAccent", "#A18BCF", "#B4A1FF", "#65538E"); Map(result, "UiAccentForeground", "#171125"); Map(result, "UiAccentSoft", "#272239", "#36284E", "#201F32"); Map(result, "UiAccentText", "#C4AAFF", "#CCBEFF", "#BEABD8", "#A698BD");
            Map(result, "UiHover", "#20283A", "#313B53"); Map(result, "UiSelection", "#564B83"); Map(result, "UiSuccess", "#85DCC0", "#8DE2C5", "#89DDC4", "#8EDCC5", "#92BDAF"); Map(result, "UiSuccessSoft", "#18322E", "#1B3936", "#162B2A", "#2B4946", "#30423F"); Map(result, "UiWarning", "#E9C985", "#E5BB77"); Map(result, "UiWarningSoft", "#322A1C", "#80623E"); Map(result, "UiError", "#F0BB9B"); Map(result, "UiErrorSoft", "#3A241F");
            return result;
        }
        static void Map(Dictionary<string, string> map, string key, params string[] values) { foreach (string value in values) map[value] = key; }
        sealed class PaletteColor : DependencyObject
        {
            public static readonly DependencyProperty ValueProperty = DependencyProperty.Register("Value", typeof(Color), typeof(PaletteColor));
            public Color Value { get { return (Color)GetValue(ValueProperty); } set { SetValue(ValueProperty, value); } }
        }
    }

    public partial class MainController
    {
        UiAppearance appearance;
        bool changingAppearance;

        void InitializeAppearance()
        {
            appearance = new UiAppearance(window.Resources);
            string[] saved = UiAppearance.LoadPreferences(engine.DataRoot);
            appearance.Apply(saved[0], saved[1]);
            var themes = window.FindName("ThemePicker") as ComboBox; var accents = window.FindName("AccentPicker") as ComboBox;
            var status = window.FindName("AppearanceStatus") as TextBlock;
            if (themes == null || accents == null) return;
            changingAppearance = true; themes.Items.Clear(); accents.Items.Clear();
            foreach (string item in UiAppearance.Themes) themes.Items.Add(item);
            foreach (string item in UiAppearance.Accents) accents.Items.Add(item);
            themes.SelectedItem = appearance.Theme; accents.SelectedItem = appearance.Accent; changingAppearance = false;
            Action apply = delegate
            {
                if (changingAppearance) return;
                appearance.Apply(themes.SelectedItem as string, accents.SelectedItem as string);
                SetAppearanceTitleBar(window, appearance.Theme == "Dark");
                try
                {
                    UiAppearance.SavePreferences(engine.DataRoot, appearance.Theme, appearance.Accent);
                    if (status != null) status.Text = "Appearance saved.";
                }
                catch (Exception error) { if (status != null) status.Text = "Could not save appearance: " + error.Message; }
            };
            themes.SelectionChanged += delegate { apply(); };
            accents.SelectionChanged += delegate { apply(); };
            window.SourceInitialized += delegate { SetAppearanceTitleBar(window, appearance.Theme == "Dark"); };
            if (status != null) status.Text = "Choose a theme and accent color.";
        }

        [DllImport("dwmapi.dll", EntryPoint = "DwmSetWindowAttribute", PreserveSig = true)]
        static extern int SetAppearanceDwmAttribute(IntPtr hwnd, int attribute, ref int value, int size);
        static void SetAppearanceTitleBar(Window target, bool dark)
        {
            try { int enabled = dark ? 1 : 0; SetAppearanceDwmAttribute(new WindowInteropHelper(target).Handle, 20, ref enabled, sizeof(int)); } catch { }
        }
    }
}
