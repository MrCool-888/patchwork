using System;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Patchwork
{
    internal static class AppearanceTests
    {
        internal static void Run(string root)
        {
            var first = new UiAppearance(new ResourceDictionary());
            var second = new UiAppearance(new ResourceDictionary());
            var shared = first.Semantic("UiSurface");
            var before = shared.Color;
            first.Apply("Light", "Mint");
            Assert(Object.ReferenceEquals(shared, first.Semantic("UiSurface")) && shared.Color != before, "Existing controls would not observe palette changes.");
            Assert(second.Semantic("UiSurface").Color != shared.Color, "Appearance state leaked across windows.");
            foreach (string theme in UiAppearance.Themes)
                foreach (string accent in UiAppearance.Accents)
                {
                    first.Apply(theme, accent);
                    ValidateIcon(AppIcons.Render(first.Semantic("UiAccent").Color, first.Semantic("UiAccentForeground").Color, 256), first.Semantic("UiAccent").Color, first.Semantic("UiAccentForeground").Color);
                    foreach (string surface in new[] { "UiSurface", "UiSurfaceAlt", "UiSidebar", "UiAccentSoft" })
                    {
                        Assert(Contrast(first, "UiText", surface) >= 4.5, theme + "/" + accent + " primary text on " + surface + " is low contrast.");
                        Assert(Contrast(first, "UiMuted", surface) >= 4.5, theme + "/" + accent + " muted text on " + surface + " is low contrast.");
                        Assert(Contrast(first, "UiSubtle", surface) >= 4.5, theme + "/" + accent + " subtle text on " + surface + " is low contrast.");
                    }
                    foreach (string surface in new[] { "UiBackground", "UiSurface", "UiAccentSoft" })
                        Assert(Contrast(first, "UiAccentText", surface) >= 4.5, theme + "/" + accent + " accent text on " + surface + " is low contrast.");
                    Assert(UiAppearance.Contrast(first.Semantic("UiAccent").Color.ToString(), first.Semantic("UiAccentForeground").Color.ToString()) >= 4.5, theme + "/" + accent + " primary button text contrast is low.");
                    Assert(UiAppearance.Contrast("#FFFFFF", first.Semantic("UiSelection").Color.ToString()) >= 4.5, theme + "/" + accent + " white selection text contrast is low.");
                    Assert(Contrast(first, "UiBorder", "UiInput") >= 3, theme + "/" + accent + " input outlines are too faint.");
                    Assert(Contrast(first, "UiSuccess", "UiSuccessSoft") >= 4.5, theme + "/" + accent + " success contrast is low.");
                    Assert(Contrast(first, "UiWarning", "UiWarningSoft") >= 4.5, theme + "/" + accent + " warning contrast is low.");
                    Assert(Contrast(first, "UiError", "UiErrorSoft") >= 4.5, theme + "/" + accent + " error contrast is low.");
                    Assert(first.Semantic("UiSuccess").Color != first.Semantic("UiAccent").Color && first.Semantic("UiWarning").Color != first.Semantic("UiAccent").Color, "Status colors must remain distinct from the accent.");
                }
            string path = Path.Combine(root, "appearance-" + Guid.NewGuid().ToString("N"));
            try
            {
                UiAppearance.SavePreferences(path, "Light", "Blue");
                var saved = UiAppearance.LoadPreferences(path);
                Assert(saved[0] == "Light" && saved[1] == "Blue", "Appearance preferences did not persist.");
                using (var stream = System.Reflection.Assembly.GetExecutingAssembly().GetManifestResourceStream("Patchwork.MainWindow.xaml"))
                {
                    var window = (Window)System.Windows.Markup.XamlReader.Load(stream);
                    try
                    {
                        new MainController(window, path);
                        Assert(IconMatches(window.Icon as BitmapSource, Color.FromRgb(0x5A, 0xA7, 0xF5), Color.FromRgb(0x10, 0x24, 0x3A)), "Startup icon ignored the saved Blue accent.");
                    }
                    finally { window.Close(); }
                }
                File.WriteAllText(Path.Combine(path, "appearance.json"), "{");
                saved = UiAppearance.LoadPreferences(path);
                Assert(saved[0] == UiAppearance.DefaultTheme && saved[1] == UiAppearance.DefaultAccent, "Invalid appearance preferences did not fall back safely.");
            }
            finally { if (Directory.Exists(path)) Directory.Delete(path, true); }
        }
        static double Contrast(UiAppearance appearance, string foreground, string background)
        {
            return UiAppearance.Contrast(appearance.Semantic(foreground).Color.ToString(), appearance.Semantic(background).Color.ToString());
        }
        internal static bool IconMatches(BitmapSource image, Color background, Color foreground)
        {
            if (image == null || image.PixelWidth != 256 || image.PixelHeight != 256 || !image.IsFrozen) return false;
            byte[] pixels = new byte[256 * 256 * 4]; image.CopyPixels(pixels, 256 * 4, 0);
            return pixels[3] == 0 && PixelMatches(pixels, 128, 128, background) && PixelMatches(pixels, 80, 128, foreground);
        }
        static void ValidateIcon(BitmapSource image, Color background, Color foreground) { Assert(IconMatches(image, background, foreground), "App icon has wrong palette colors or loses transparent corners."); }
        static bool PixelMatches(byte[] pixels, int x, int y, Color color)
        {
            int offset = (y * 256 + x) * 4;
            return pixels[offset] == color.B && pixels[offset + 1] == color.G && pixels[offset + 2] == color.R && pixels[offset + 3] == 255;
        }
        static void Assert(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    }
}
