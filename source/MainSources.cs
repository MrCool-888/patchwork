using System;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace Patchwork
{
    public partial class MainController
    {
        PatchSourceStore patchSources;
        string sourceLoadError;
        bool checkingSources, catalogRefreshPending, closed;
        DispatcherTimer sourceTimer;
        void InitializeSources()
        {
            try { patchSources = new PatchSourceStore(engine.DataRoot); }
            catch (Exception error) { sourceLoadError = "Saved patch sources could not be read: " + error.Message; }
            Control<Button>("SourcesNav").Click += delegate { ShowPage("sources"); };
            Control<Button>("SourcesButton").Click += delegate { ShowPage("sources"); };
            Control<Button>("EmptySourcesButton").Click += delegate { ShowPage("sources"); };
            Control<Button>("AddSourceButton").Click += async delegate { await AddSource(); };
            Control<Button>("CheckSourcesButton").Click += async delegate { await CheckSources(true, null); };
            sourceTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(5) };
            sourceTimer.Tick += async delegate { await CheckSources(false, null); };
            window.Loaded += delegate { sourceTimer.Start(); };
            window.Closed += delegate { closed = true; sourceTimer.Stop(); };
        }
        async Task AddSource()
        {
            if (busy || checkingSources || patchSources == null) return;
            try
            {
                var source = patchSources.Add(Control<TextBox>("SourceLinkBox").Text, Control<CheckBox>("SourcePrereleases").IsChecked == true);
                Control<TextBox>("SourceLinkBox").Clear(); RenderSources();
                await CheckSources(true, source.Repository);
            }
            catch (Exception error) { Notify("Could not add source: " + error.Message, true); RenderSources(); }
        }
        async Task CheckSources(bool manual, string repository)
        {
            if (busy || checkingSources || patchSources == null || closed) return;
            var due = patchSources.Sources.Where(x => repository == null || x.Repository.Equals(repository, StringComparison.OrdinalIgnoreCase)).Where(x => manual || IsSourceDue(x, DateTime.UtcNow)).ToList();
            if (due.Count == 0) { if (manual) Notify("Add a patch source first."); return; }
            checkingSources = true; Control<Grid>("SourcesPage").IsEnabled = false;
            Control<Button>("ImportButton").IsEnabled = false;
            Control<TextBlock>("SourcesStatus").Text = "Checking GitHub patch releases…";
            int updated = 0, errors = 0;
            try
            {
                foreach (var source in due)
                {
                    try
                    {
                        var snapshot = Json.Serializer.Deserialize<PatchSource>(Json.Pretty(source));
                        var download = await Task.Run(() => PatchSources.Download(snapshot, engine.DataRoot));
                        if (closed) return;
                        int changes = patchSources.Apply(download); updated += changes;
                        if (changes > 0) catalogRefreshPending = true;
                    }
                    catch (Exception error)
                    {
                        if (closed) return;
                        errors++;
                        try { patchSources.RecordError(source.Repository, error.Message); }
                        catch (Exception saveError) { Notify("Could not save source status: " + saveError.Message, true); }
                    }
                }
                if (!busy && catalogRefreshPending) RefreshSourceCatalog();
                RenderSources();
                string result = updated + " patch pack(s) updated" + (errors == 0 ? "." : "; " + errors + " source check(s) need attention.");
                Control<TextBlock>("SourcesStatus").Text = result;
                if (manual || updated > 0) Notify(result + (updated > 0 ? " Preview to update applied patches." : ""), errors > 0);
            }
            finally { checkingSources = false; if (!closed) { Control<Grid>("SourcesPage").IsEnabled = !busy; Control<Button>("ImportButton").IsEnabled = !busy; } }
        }
        internal static bool IsSourceDue(PatchSource source, DateTime utc)
        {
            DateTime checkedAt;
            return source.Automatic && (!DateTime.TryParse(source.LastCheckUtc, null, System.Globalization.DateTimeStyles.RoundtripKind, out checkedAt) || utc - checkedAt.ToUniversalTime() >= TimeSpan.FromHours(1));
        }
        void RefreshSourceCatalog()
        {
            string oldId = current == null ? null : current.Id;
            string oldPage = page;
            var oldSelection = selected.ToArray();
            bundles.Clear(); LoadRecipes(); RefreshPicker();
            int index = bundles.FindIndex(x => x.Id == oldId);
            SetBundle(index < 0 && bundles.Count > 0 ? 0 : index);
            if (current != null && current.Id == oldId && oldSelection.Length > 0)
            {
                selected.Clear(); foreach (string id in oldSelection.Where(x => current.Patches.Any(p => p.Id == x && p.Ready))) SelectDependencies(current.Patches.Single(x => x.Id == id));
                RenderPatches(); UpdateSession();
            }
            catalogRefreshPending = false;
            if (oldPage != "preview") ShowPage(oldPage);
        }
        void RenderSources()
        {
            var list = Control<StackPanel>("SourcesList"); list.Children.Clear();
            Control<Button>("AddSourceButton").IsEnabled = patchSources != null;
            Control<Button>("CheckSourcesButton").IsEnabled = patchSources != null && patchSources.Sources.Count > 0;
            if (patchSources == null) { list.Children.Add(Text(sourceLoadError, 13, "#F0BB9B")); return; }
            if (patchSources.Sources.Count == 0) { list.Children.Add(Text("No patch sources yet. Paste a repository link above to download its published patch files.", 14, "#929DB1")); return; }
            foreach (var source in patchSources.Sources)
            {
                var card = new Border { Background = Color("#151D2B"), BorderBrush = Color("#2B354A"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(12), Padding = new Thickness(21), Margin = new Thickness(0, 0, 6, 14) };
                var stack = new StackPanel(); var title = Text(source.Repository, 18, "#E8EDF6"); title.FontWeight = FontWeights.SemiBold; stack.Children.Add(title);
                var packs = Text(source.Packs.Count == 0 ? "No packs downloaded yet" : String.Join("  ·  ", source.Packs.Select(x => x.BundleId + " v" + x.Version)), 12, "#BEABD8"); packs.Margin = new Thickness(0, 9, 0, 0); stack.Children.Add(packs);
                DateTime check; string last = DateTime.TryParse(source.LastCheckUtc, null, System.Globalization.DateTimeStyles.RoundtripKind, out check) ? check.ToLocalTime().ToString("MMM d · h:mm tt") : "Never";
                var status = Text((source.Status ?? "") + "\nLast checked: " + last, 12, "#929DB1"); status.Margin = new Thickness(0, 10, 0, 14); stack.Children.Add(status);
                var automatic = new CheckBox { Content = "Automatically update this source", IsChecked = source.Automatic, Margin = new Thickness(0, 0, 0, 10), FontSize = 12 };
                var prereleases = new CheckBox { Content = "Include pre-release patch packs (experimental)", IsChecked = source.IncludePrereleases, Margin = new Thickness(0, 0, 0, 16), FontSize = 12 };
                RoutedEventHandler options = delegate { try { patchSources.Options(source.Repository, automatic.IsChecked == true, prereleases.IsChecked == true); } catch (Exception error) { Notify(error.Message, true); RenderSources(); } };
                automatic.Click += options; prereleases.Click += options; stack.Children.Add(automatic); stack.Children.Add(prereleases);
                var buttons = new WrapPanel();
                var checkButton = new Button { Content = "Check now", Margin = new Thickness(0, 0, 10, 6), FontSize = 12 };
                checkButton.Click += async delegate { await CheckSources(true, source.Repository); }; buttons.Children.Add(checkButton);
                var open = new Button { Content = "Open repository", Margin = new Thickness(0, 0, 10, 6), FontSize = 12 };
                open.Click += delegate { try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(source.Link) { UseShellExecute = true }); } catch (Exception error) { Notify(error.Message, true); } }; buttons.Children.Add(open);
                var remove = new Button { Content = "Remove source", Background = System.Windows.Media.Brushes.Transparent, Margin = new Thickness(0, 0, 0, 6), FontSize = 12 };
                remove.Click += delegate { try { patchSources.Remove(source.Repository); RenderSources(); Notify("Source removed. Downloaded patches and applied history were kept."); } catch (Exception error) { Notify(error.Message, true); } }; buttons.Children.Add(remove);
                stack.Children.Add(buttons); card.Child = stack; list.Children.Add(card);
            }
        }
    }
}
