using Compositor.App.Platform;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Compositor.App.Controls;

public enum UpdateChoice { Later, Install, Skip }

/// <summary>The "new version available" prompt and the download progress that follows it.</summary>
public static class UpdateDialog
{
    public static async Task<UpdateChoice> Ask(XamlRoot root, UpdateInfo update)
    {
        var body = new StackPanel { Spacing = 12, Width = 440 };
        body.Children.Add(new TextBlock
        {
            Text = $"Layer Form {update.VersionText} is ready to download. You have version {Updater.CurrentText}.",
            TextWrapping = TextWrapping.Wrap,
        });
        if (!string.IsNullOrWhiteSpace(update.Notes))
        {
            body.Children.Add(Ui.Label("What’s new", 12, true));
            body.Children.Add(new ScrollViewer
            {
                MaxHeight = 220, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Content = new TextBlock { Text = Tidy(update.Notes), TextWrapping = TextWrapping.Wrap, FontSize = 12, IsTextSelectionEnabled = true },
            });
        }
        var closing = Ui.Label("Layer Form closes to install the update and reopens when it’s done. You’ll be asked to save open projects first.", 12, brush: Ui.Secondary);
        closing.TextWrapping = TextWrapping.Wrap;
        body.Children.Add(closing);

        var dialog = new ContentDialog
        {
            XamlRoot = root, Title = "Update available", Content = body,
            PrimaryButtonText = "Download and Install", SecondaryButtonText = "Skip This Version", CloseButtonText = "Later",
            DefaultButton = ContentDialogButton.Primary,
        };
        return await dialog.ShowAsync() switch
        {
            ContentDialogResult.Primary => UpdateChoice.Install,
            ContentDialogResult.Secondary => UpdateChoice.Skip,
            _ => UpdateChoice.Later,
        };
    }

    /// <summary>Downloads the installer with a progress bar; null when cancelled or failed.</summary>
    public static async Task<string?> Download(XamlRoot root, UpdateInfo update)
    {
        var bar = new ProgressBar { Minimum = 0, Maximum = 1, Width = 380, IsIndeterminate = true };
        var status = Ui.Label("Connecting…", 12, brush: Ui.Secondary);
        var dialog = new ContentDialog
        {
            XamlRoot = root, Title = $"Downloading Layer Form {update.VersionText}",
            Content = new StackPanel { Spacing = 10, Children = { bar, status } }, CloseButtonText = "Cancel",
        };
        var cancel = new CancellationTokenSource();
        var queue = dialog.DispatcherQueue;
        string? path = null, error = null;
        dialog.Opened += async (_, _) =>
        {
            try
            {
                path = await Updater.DownloadAsync(update, (done, total) => queue.TryEnqueue(() =>
                {
                    bar.IsIndeterminate = total <= 0;
                    if (total > 0) bar.Value = (double)done / total;
                    status.Text = total > 0 ? $"{Megabytes(done)} of {Megabytes(total)}" : Megabytes(done);
                }), cancel.Token);
            }
            catch (OperationCanceledException) { }
            catch (Exception e) { error = e.Message; Diagnostics.Log("Update download: " + e.Message); }
            dialog.Hide();
        };
        await dialog.ShowAsync();
        cancel.Cancel();
        if (error != null) await Dialogs.Error(root, "The update couldn’t be downloaded", error);
        return error == null ? path : null;
    }

    private static string Megabytes(long bytes) => $"{bytes / 1048576.0:0.0} MB";

    /// <summary>Release notes are Markdown; drop heading markers, bold and link targets so they read as plain text.</summary>
    private static string Tidy(string markdown) => string.Join("\n", markdown.Replace("\r", "").Split('\n')
        .Select(line => MarkdownLink.Replace(line.TrimStart('#', ' '), "$1").Replace("**", "").Replace("`", ""))).Trim();

    private static readonly System.Text.RegularExpressions.Regex MarkdownLink = new(@"\[([^\]]+)\]\([^)]+\)");
}
