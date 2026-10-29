using System.Diagnostics;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage.Pickers;

namespace Tally;

/// <summary>
/// The shell's part (IShell): the folder picker, a question, the clipboard's image, and opening a file, a folder or a
/// settings page. Questions and pickers belong to Tally's window, which <paramref name="window"/> makes if there's none.
/// </summary>
internal sealed class Shell(Func<MainWindow> window) : IShell
{
    public async Task<string?> ChooseFolder(string prompt)
    {
        var picker = new FolderPicker { SuggestedStartLocation = PickerLocationId.DocumentsLibrary, CommitButtonText = prompt };
        picker.FileTypeFilter.Add("*");
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(window()));
        var folder = await picker.PickSingleFolderAsync();
        return folder?.Path;
    }

    /// <summary>Asked in the window, shown first: a question on a window that isn't would never be answered, and the
    /// window's next ones would wait for it.</summary>
    public Task<bool> Confirm(string title, string message, string confirm, string cancel)
    {
        window().Present();
        return Dialogs.Confirm(title, message, confirm, cancel);
    }

    public async Task<byte[]?> ClipboardImage()
    {
        var content = Clipboard.GetContent();
        if (!content.Contains(StandardDataFormats.Bitmap)) return null;
        var reference = await content.GetBitmapAsync();
        using var source = await reference.OpenReadAsync();
        var decoder = await Windows.Graphics.Imaging.BitmapDecoder.CreateAsync(source);
        using var bitmap = await decoder.GetSoftwareBitmapAsync();
        using var png = new Windows.Storage.Streams.InMemoryRandomAccessStream();
        var encoder = await Windows.Graphics.Imaging.BitmapEncoder.CreateAsync(Windows.Graphics.Imaging.BitmapEncoder.PngEncoderId, png);
        encoder.SetSoftwareBitmap(bitmap);
        await encoder.FlushAsync();
        var bytes = new byte[png.Size];
        using var stream = png.AsStreamForRead();
        stream.Position = 0;
        await stream.ReadExactlyAsync(bytes);
        return bytes;
    }

    public void Open(string target) => Launch(target);

    /// <summary>Opened by Explorer, as a double-click would be, so what it starts isn't Tally's child: the recorder leaves
    /// Tally's process tree out of the system audio, so a call in a browser Tally started would go unheard.</summary>
    public static void Launch(string target) => Process.Start("explorer.exe", $"\"{target}\"")?.Dispose();
}
