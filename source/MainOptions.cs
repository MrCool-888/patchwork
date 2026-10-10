using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;

namespace Patchwork
{
    public partial class MainController
    {
        readonly Dictionary<string, string> colorOptions = new Dictionary<string, string>();
        void LoadColorOptions()
        {
            try { string file = Path.Combine(engine.DataRoot, "patch-options.json"); if (File.Exists(file)) foreach (var pair in Json.Parse(PatchEngine.Decode(PatchEngine.Read(file)))) colorOptions[pair.Key] = ThemeOptions.Color(pair.Value as string); } catch { colorOptions.Clear(); }
        }
        string OptionValue(PatchDefinition patch, PatchColorOption option)
        {
            string value; return colorOptions.TryGetValue(current.Id + "|" + patch.Id + "." + option.Id, out value) ? value : option.Default;
        }
        Dictionary<string, string> SelectedOptions()
        {
            var values = new Dictionary<string, string>();
            foreach (var patch in current.Patches.Where(x => selected.Contains(x.Id))) foreach (var option in patch.Options) values.Add(patch.Id + "." + option.Id, ThemeOptions.Color(OptionValue(patch, option)));
            return values;
        }
        void AddColorOptions(StackPanel content, PatchDefinition patch)
        {
            foreach (var option in patch.Options)
            {
                string key = current.Id + "|" + patch.Id + "." + option.Id;
                var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 12, 0, 0) };
                var label = Text(option.Label, 12, "#C2CBDC"); label.Width = 116; label.VerticalAlignment = VerticalAlignment.Center;
                var input = new TextBox { Text = OptionValue(patch, option), Width = 105, MaxLength = 7, ToolTip = "Color in #RRGGBB format" };
                var swatch = new Border { Background = Color(option.Default), Width = 22, Height = 22, CornerRadius = new CornerRadius(5), Margin = new Thickness(10, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center };
                try { swatch.Background = Color(ThemeOptions.Color(input.Text)); } catch (InvalidDataException) { }
                var choose = new Button { Content = "Choose…", Padding = new Thickness(8, 4, 8, 4) };
                input.TextChanged += delegate {
                    preview = null; colorOptions[key] = input.Text;
                    try { string value = ThemeOptions.Color(input.Text); swatch.Background = Color(value); input.ToolTip = "Color in #RRGGBB format"; var valid = colorOptions.Where(x => System.Text.RegularExpressions.Regex.IsMatch(x.Value ?? "", "^#[0-9a-fA-F]{6}\\z")).ToDictionary(x => x.Key, x => x.Value); File.WriteAllText(Path.Combine(engine.DataRoot, "patch-options.json"), Json.Pretty(valid), new UTF8Encoding(false)); }
                    catch (InvalidDataException) { input.ToolTip = "Enter a complete color such as #8A66FF before previewing."; }
                    catch (Exception error) { Notify("Could not save color: " + error.Message, true); }
                };
                choose.Click += delegate { using (var dialog = new System.Windows.Forms.ColorDialog()) { dialog.FullOpen = true; try { dialog.Color = System.Drawing.ColorTranslator.FromHtml(ThemeOptions.Color(input.Text)); } catch { } if (dialog.ShowDialog(new NativeOwner(new System.Windows.Interop.WindowInteropHelper(window).Handle)) == System.Windows.Forms.DialogResult.OK) input.Text = "#" + dialog.Color.R.ToString("X2") + dialog.Color.G.ToString("X2") + dialog.Color.B.ToString("X2"); } };
                row.Children.Add(label); row.Children.Add(input); row.Children.Add(swatch); row.Children.Add(choose); content.Children.Add(row);
            }
        }
    }
}
