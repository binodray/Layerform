using System.Runtime.InteropServices;
using Compositor.Editing;
using Compositor.Imaging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage.Streams;

namespace Compositor.App.Platform;

/// <summary>The session's window services on Windows: sounds, dialogs and the clipboard.</summary>
public sealed class WindowsHost : IEditorHost
{
    private readonly Func<XamlRoot?> xamlRoot;
    private RasterImage? cachedClipboard;
    private long cachedSequence = -1;

    public WindowsHost(Func<XamlRoot?> xamlRoot) { this.xamlRoot = xamlRoot; }

    [DllImport("user32.dll")] private static extern bool MessageBeep(uint type);
    [DllImport("user32.dll")] private static extern uint GetClipboardSequenceNumber();

    public void Beep() => MessageBeep(0);

    public async Task<bool?> AskBakeLiveMasks(bool plural)
    {
        if (xamlRoot() is not { } root) return null;
        var dialog = new ContentDialog
        {
            XamlRoot = root,
            Title = plural ? "These layers supply live masks" : "This layer supplies a live mask",
            Content = new TextBlock
            {
                Text = "Bake keeps the current masked appearance in the dependent layers’ pixels. Remove Links reveals their pixels. You can undo either choice.",
                TextWrapping = TextWrapping.Wrap,
            },
            PrimaryButtonText = "Bake and Delete",
            SecondaryButtonText = "Remove Links and Delete",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
        };
        var result = await dialog.ShowAsync();
        return result switch { ContentDialogResult.Primary => true, ContentDialogResult.Secondary => false, _ => null };
    }

    public long ClipboardSequence => GetClipboardSequenceNumber();

    public void SetClipboardImage(RasterImage image)
    {
        try
        {
            var png = Codecs.EncodePng(image);
            var stream = new InMemoryRandomAccessStream();
            using (var writer = new DataWriter(stream.GetOutputStreamAt(0)))
            {
                writer.WriteBytes(png);
                writer.StoreAsync().AsTask().GetAwaiter().GetResult();
                writer.DetachStream();
            }
            var package = new DataPackage { RequestedOperation = DataPackageOperation.Copy };
            package.SetBitmap(RandomAccessStreamReference.CreateFromStream(stream));
            package.SetData("PNG", stream.CloneStream());
            Clipboard.SetContent(package);
            Clipboard.Flush();
            cachedClipboard = image;
            cachedSequence = ClipboardSequence;
        }
        catch (Exception e) { Diagnostics.Log("Clipboard write failed: " + e.Message); }
    }

    public bool ClipboardHasImage
    {
        get
        {
            try
            {
                var content = Clipboard.GetContent();
                return content.Contains(StandardDataFormats.Bitmap) || content.Contains("PNG") || content.Contains(StandardDataFormats.StorageItems);
            }
            catch { return false; }
        }
    }

    /// <summary>The clipboard's image, read ahead of Paste by <see cref="RefreshClipboardAsync"/>.</summary>
    public RasterImage? GetClipboardImage() => cachedSequence == ClipboardSequence ? cachedClipboard : null;

    public async Task RefreshClipboardAsync()
    {
        long sequence = ClipboardSequence;
        if (sequence == cachedSequence && cachedClipboard != null) return;
        try
        {
            var content = Clipboard.GetContent();
            byte[]? bytes = null;
            if (content.Contains("PNG") && await content.GetDataAsync("PNG") is IRandomAccessStream pngStream)
                bytes = await ReadAll(pngStream);
            else if (content.Contains(StandardDataFormats.Bitmap))
            {
                var reference = await content.GetBitmapAsync();
                using var stream = await reference.OpenReadAsync();
                bytes = await ReadAll(stream);
            }
            else if (content.Contains(StandardDataFormats.StorageItems))
            {
                var items = await content.GetStorageItemsAsync();
                if (items.OfType<Windows.Storage.StorageFile>().FirstOrDefault() is { } file)
                {
                    var asset = await ImageImport.DecodeAsync(file.Path, 100_000_000);
                    cachedClipboard = asset.Image;
                    cachedSequence = sequence;
                    return;
                }
            }
            if (bytes != null)
            {
                cachedClipboard = await ImageImport.DecodeBytesAsync(bytes);
                cachedSequence = sequence;
            }
        }
        catch (Exception e) { Diagnostics.Log("Clipboard read failed: " + e.Message); }
    }

    private static async Task<byte[]> ReadAll(IRandomAccessStream stream)
    {
        stream.Seek(0);
        var buffer = new byte[stream.Size];
        using var reader = new DataReader(stream.GetInputStreamAt(0));
        await reader.LoadAsync((uint)stream.Size);
        reader.ReadBytes(buffer);
        return buffer;
    }
}
