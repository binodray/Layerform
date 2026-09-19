using Compositor.App.Platform;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Compositor.App.Controls;

/// <summary>Remove Background's models: on first use it asks which to download (with sizes and a recommendation);
/// later it switches the active model, checks for newer versions, updates, downloads or removes them.</summary>
public static class ModelsDialog
{
    /// <returns>True when a model is installed and active afterwards.</returns>
    public static async Task<bool> Show(XamlRoot root, bool firstUse)
    {
        var cancel = new CancellationTokenSource();
        bool busy = false;
        var dialog = new ContentDialog
        {
            XamlRoot = root,
            Title = firstUse ? "Download a background removal model" : "Background Removal Models",
            PrimaryButtonText = firstUse ? "Continue" : "Done",
            CloseButtonText = firstUse ? "Not now" : null,
            DefaultButton = ContentDialogButton.Primary,
        };
        dialog.Resources["ContentDialogMaxWidth"] = 640.0;
        var status = Ui.Label("", 12);
        status.TextWrapping = TextWrapping.Wrap;
        status.Foreground = Ui.Secondary;
        var rows = new StackPanel { Spacing = 10 };
        var refreshers = new List<Action>();
        var updates = new Dictionary<string, string?>();
        var bars = new Dictionary<string, ProgressBar>();

        void Refresh()
        {
            foreach (var r in refreshers) r();
            dialog.IsPrimaryButtonEnabled = !busy && (!firstUse || BackgroundModels.AnyInstalled);
        }

        async Task Download(IReadOnlyList<BackgroundModel> models)
        {
            if (busy) return;
            busy = true;
            Refresh();
            try
            {
                foreach (var model in models)
                {
                    var bar = bars[model.Id];
                    bar.Visibility = Visibility.Visible;
                    bar.Value = 0;
                    var progress = new Progress<(long Done, long Total)>(p =>
                    {
                        bar.Value = p.Total > 0 ? 100.0 * p.Done / p.Total : 0;
                        status.Text = $"Downloading {model.Name}: {p.Done / 1e6:0} of {p.Total / 1e6:0} MB…";
                    });
                    await BackgroundModels.Download(model, progress, cancel.Token);
                    bar.Visibility = Visibility.Collapsed;
                    updates.Remove(model.Id);
                }
                status.Text = $"Ready. Remove Background uses {BackgroundModels.Active?.Name}.";
            }
            catch (OperationCanceledException) { status.Text = "Download cancelled."; BackgroundModels.CleanPartialDownloads(); }
            catch (Exception e) { status.Text = "Couldn't download: " + e.Message; BackgroundModels.CleanPartialDownloads(); }
            finally
            {
                foreach (var b in bars.Values) b.Visibility = Visibility.Collapsed;
                busy = false;
                Refresh();
            }
        }

        foreach (var model in BackgroundModels.Catalog)
        {
            var m = model;
            var use = new RadioButton { GroupName = "model", MinWidth = 0, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 2, 0, 0) };
            ToolTipService.SetToolTip(use, "Use this model for Remove Background");
            use.Checked += (_, _) => { if (BackgroundModels.IsInstalled(m.Id) && BackgroundModels.State.Active != m.Id) { BackgroundModels.Activate(m.Id); status.Text = $"Remove Background now uses {m.Name}."; Refresh(); } };
            var name = Ui.Label(m.Name, 14, true);
            var badge = new Border
            {
                CornerRadius = new CornerRadius(8), Padding = new Thickness(7, 1, 7, 2), Background = new SolidColorBrush(Ui.Accent),
                Child = Ui.Label("Recommended", 10, true), Visibility = m.Recommended ? Visibility.Visible : Visibility.Collapsed,
                VerticalAlignment = VerticalAlignment.Center,
            };
            var size = Ui.Label(m.SizeText, 12);
            size.Foreground = Ui.Secondary;
            var summary = Ui.Label(m.Summary, 12);
            summary.TextWrapping = TextWrapping.Wrap;
            summary.Foreground = Ui.Secondary;
            var state = Ui.Label("", 11);
            var action = Ui.Capsule("Download", () => { }, accent: m.Recommended);
            var remove = Ui.Capsule("Remove", () => { });
            var bar = new ProgressBar { Minimum = 0, Maximum = 100, Visibility = Visibility.Collapsed };
            bars[m.Id] = bar;
            action.Click += async (_, _) => await Download(new[] { m });
            remove.Click += (_, _) =>
            {
                if (busy) return;
                BackgroundModels.Remove(m.Id);
                status.Text = $"Removed {m.Name}.";
                Refresh();
            };
            refreshers.Add(() =>
            {
                bool installed = BackgroundModels.IsInstalled(m.Id);
                use.IsEnabled = installed && !busy;
                use.IsChecked = BackgroundModels.State.Active == m.Id;
                bool update = updates.TryGetValue(m.Id, out var latest) && latest != null;
                action.Content = !installed ? $"Download · {m.SizeText}" : update ? "Update" : "Up to date";
                action.IsEnabled = !busy && (!installed || update);
                action.Visibility = installed && !update && !updates.ContainsKey(m.Id) ? Visibility.Collapsed : Visibility.Visible;
                remove.Visibility = installed ? Visibility.Visible : Visibility.Collapsed;
                remove.IsEnabled = !busy;
                state.Text = !installed ? "Not downloaded"
                    : $"Installed {BackgroundModels.State.Models[m.Id].Downloaded:d} · version {BackgroundModels.State.Models[m.Id].Revision[..Math.Min(7, BackgroundModels.State.Models[m.Id].Revision.Length)]}"
                      + (update ? " · a newer version is available" : updates.ContainsKey(m.Id) ? " · up to date" : "")
                      + (BackgroundModels.State.Active == m.Id ? " · in use" : "");
                state.Foreground = update ? new SolidColorBrush(Colors.Orange) : installed ? new SolidColorBrush(Colors.LightGreen) : Ui.Secondary;
            });
            var text = new StackPanel { Spacing = 4, Children = { Ui.Row(8, name, badge, size), summary, state, bar } };
            var buttons = Ui.Row(8, remove, action);
            buttons.VerticalAlignment = VerticalAlignment.Center;
            var grid = new Grid { ColumnSpacing = 10 };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition());
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.Children.Add(use);
            Grid.SetColumn(text, 1);
            grid.Children.Add(text);
            Grid.SetColumn(buttons, 2);
            grid.Children.Add(buttons);
            rows.Children.Add(new Border
            {
                Child = grid, Padding = new Thickness(14, 12, 14, 12), CornerRadius = new CornerRadius(10),
                Background = Ui.Solid(18, 255, 255, 255), BorderBrush = Ui.Solid(30, 255, 255, 255), BorderThickness = new Thickness(1),
            });
        }

        long both = BackgroundModels.Catalog.Sum(m => m.Bytes);
        var downloadBoth = Ui.Capsule($"Download both · {both / 1e6:0} MB", () => { });
        downloadBoth.Click += async (_, _) => await Download(BackgroundModels.Catalog.Where(m => !BackgroundModels.IsInstalled(m.Id)).ToList());
        var check = Ui.Capsule("Check for updates", () => { });
        check.Click += async (_, _) =>
        {
            if (busy) return;
            busy = true;
            Refresh();
            status.Text = "Checking for newer versions…";
            try
            {
                int newer = 0;
                foreach (var m in BackgroundModels.Catalog.Where(m => BackgroundModels.IsInstalled(m.Id)))
                {
                    var latest = await BackgroundModels.LatestRevision(m, cancel.Token);
                    bool isNewer = latest != BackgroundModels.State.Models[m.Id].Revision;
                    updates[m.Id] = isNewer ? latest : null;
                    if (isNewer) newer++;
                }
                status.Text = newer == 0 ? "Your models are up to date." : $"{newer} update{(newer == 1 ? " is" : "s are")} available — press Update.";
            }
            catch (Exception e) { status.Text = "Couldn't check for updates: " + e.Message; }
            finally { busy = false; Refresh(); }
        };
        refreshers.Add(() =>
        {
            downloadBoth.Visibility = BackgroundModels.Catalog.Count(m => !BackgroundModels.IsInstalled(m.Id)) > 1 ? Visibility.Visible : Visibility.Collapsed;
            downloadBoth.IsEnabled = check.IsEnabled = !busy;
            check.Visibility = BackgroundModels.AnyInstalled ? Visibility.Visible : Visibility.Collapsed;
        });

        var gpu = GpuAdapters.Preferred().FirstOrDefault();
        var device = new ComboBox { MinWidth = 260 };
        device.Items.Add($"Graphics card{(gpu != null ? $" ({gpu.Name})" : "")} — fastest");
        device.Items.Add("CPU — slower, uses much less memory");
        device.SelectedIndex = BackgroundModels.State.UseCpu ? 1 : 0;
        device.SelectionChanged += (_, _) =>
        {
            BackgroundModels.SetUseCpu(device.SelectedIndex == 1);
            status.Text = device.SelectedIndex == 1 ? "Remove Background will run on the CPU." : "Remove Background will run on the graphics card.";
        };
        ToolTipService.SetToolTip(device, "The graphics card is quickest; the CPU needs far less memory, which helps on PCs with 16 GB or less.");
        var deviceRow = Ui.Row(10, Ui.Label("Run on", 12), device);

        var intro = Ui.Label(firstUse
            ? "Remove Background runs on your PC with BiRefNet, an open model (MIT License). Pick one to download — you only need to do this once. We recommend BiRefNet Lite; you can add the other, switch between them, or update them later from Filter › Background Removal Models."
            : "Choose the model Remove Background uses, check for newer versions, or download and remove models. Models are stored in your user folder, not in your projects.", 12);
        intro.TextWrapping = TextWrapping.Wrap;
        dialog.Content = new StackPanel
        {
            Spacing = 14, Width = 580,
            Children = { intro, rows, Ui.Row(10, downloadBoth, check), deviceRow, status },
        };
        Refresh();
        dialog.Closing += (_, e) => { if (busy) { e.Cancel = true; status.Text = "Please wait for the download to finish (or close to cancel it)."; cancel.Cancel(); } };
        await dialog.ShowAsync();
        cancel.Cancel();
        return BackgroundModels.Active != null;
    }
}
