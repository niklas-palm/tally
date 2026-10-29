using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.Storage.FileProperties;
using Windows.Storage.Pickers;
using Windows.System;

namespace Tally;

/// <summary>
/// A meeting's Files tab: what the owner keeps with the meeting, such as slides, a document, or a
/// screenshot of what was shared. Files are dropped here, pasted (Ctrl+V: files copied in File Explorer, or an image,
/// such as a screenshot taken with Win+Shift+S, kept as a PNG), or chosen with Add Files…. As in File Explorer: a
/// click selects one, a double-click, Enter or Space opens it in its app (the first look downloads it, with a spinner
/// on its tile), the arrows move, and Delete asks to delete it.
/// </summary>
internal sealed class FilesView : UserControl
{
    private static readonly Dictionary<string, Task<ImageSource?>> Kinds = [];  // each kind's icon, as Explorer draws it

    private readonly AppModel model;
    private readonly Window window;
    private readonly string meeting;
    private readonly ProgressRing ring = new() { IsActive = true, Width = 16, Height = 16 };
    private readonly TextBlock adding = Ui.Text("", brush: Ui.Secondary);
    private readonly Border body = new() { CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(2), Padding = new Thickness(6) };
    private readonly GridView grid = new() { SelectionMode = ListViewSelectionMode.Single, IsItemClickEnabled = false };
    private string drawn = "";

    public FilesView(AppModel model, Window window, string meeting)
    {
        this.model = model;
        this.window = window;
        this.meeting = meeting;
        var add = Ui.Button("Add Files…", () => _ = Choose(), glyph: Ui.AddGlyph);
        var top = Ui.Grid("Auto,Auto,*,Auto", spacing: 8).Add(ring.With(r => r.VerticalAlignment = VerticalAlignment.Center), adding.With(t => t.VerticalAlignment = VerticalAlignment.Center).At(1), add.At(3));
        Content = Ui.Column(12, top, body);
        IsTabStop = false;
        body.BorderBrush = new SolidColorBrush(Colors.Transparent);
        ScrollViewer.SetVerticalScrollMode(grid, ScrollMode.Disabled);  // the page scrolls, not the tiles
        ScrollViewer.SetVerticalScrollBarVisibility(grid, ScrollBarVisibility.Disabled);
        grid.DoubleTapped += (_, _) => { if (Selected is { } file) _ = Open(file); };
        grid.PreviewKeyDown += (_, e) =>  // before the grid, which takes Enter and Space for itself
        {
            if (Selected is not { } file) return;
            if (e.Key is VirtualKey.Enter or VirtualKey.Space) _ = Open(file);
            else if (e.Key == VirtualKey.Delete) _ = Delete(file);
            else return;
            e.Handled = true;
        };

        AllowDrop = true;
        DragOver += (_, e) =>
        {
            if (!e.DataView.Contains(StandardDataFormats.StorageItems)) return;  // a link from a browser isn't a file
            e.AcceptedOperation = DataPackageOperation.Copy;
            e.DragUIOverride.Caption = "Add to this meeting";
            body.BorderBrush = Ui.Theme("AccentFillColorDefaultBrush");  // as Explorer shows a drop
        };
        DragLeave += (_, _) => body.BorderBrush = new SolidColorBrush(Colors.Transparent);
        Drop += async (_, e) =>
        {
            body.BorderBrush = new SolidColorBrush(Colors.Transparent);
            if (!e.DataView.Contains(StandardDataFormats.StorageItems)) return;
            var deferral = e.GetDeferral();
            IReadOnlyList<IStorageItem> items;
            try { items = await e.DataView.GetStorageItemsAsync(); }
            catch (Exception)  // new Outlook offers files it hands over only later: they can't be read as they're dropped
            {
                model.Problem = new("Tally couldn't read those files. Save them to your PC first, then add them here.");
                return;
            }
            finally { deferral.Complete(); }
            var paths = items.OfType<StorageFile>().Select(f => f.Path).ToList();  // not folders
            if (paths.Count > 0) await model.AddFiles(paths, meeting);
        };
        Changed();
        _ = model.LoadFiles(meeting);  // fresh links, valid for an hour
    }

    private MeetingFile? Selected => grid.SelectedItem is GridViewItem { Tag: string id } ? model.Files.GetValueOrDefault(meeting)?.FirstOrDefault(f => f.FileId == id) : null;

    public void Changed()
    {
        var count = model.AddingFiles.GetValueOrDefault(meeting);
        ring.Visibility = adding.Visibility = count > 0 ? Visibility.Visible : Visibility.Collapsed;
        adding.Text = count == 1 ? "Adding 1 file…" : $"Adding {count} files…";
        var loaded = model.Files.GetValueOrDefault(meeting);
        var signature = string.Join("|", loaded?.Select(f => $"{f.FileId}{f.Url}{model.Opening.Contains(f.FileId)}") ?? ["none"]) + model.Offline + model.Auth.State;
        if (signature == drawn) return;
        drawn = signature;
        if (loaded is not { Count: > 0 } files)
        {
            body.Child = Ui.Placeholder(Ui.CopyGlyph, "No Files", loaded is null && (model.Offline || model.Auth.State != AuthState.SignedIn)
                ? model.Offline ? "Files already here show once you're back online." : "Sign in to see the files already here."
                : "Keep slides, documents and screenshots with this meeting: drop them here, or paste them with Ctrl+V.").With(p => p.MinHeight = 220);
            return;
        }
        var selected = Selected?.FileId;
        grid.Items.Clear();
        foreach (var file in files) grid.Items.Add(Tile(file));
        grid.SelectedItem = grid.Items.OfType<GridViewItem>().FirstOrDefault(i => i.Tag as string == selected);
        body.Child = grid;
    }

    /// <summary>A file: a picture of it (the image itself, or its kind's icon), its name, and its size and when it was added.</summary>
    private GridViewItem Tile(MeetingFile file)
    {
        var picture = new Border { Width = 130, Height = 96, CornerRadius = new CornerRadius(6), Background = Ui.Theme("ControlFillColorSecondaryBrush") };
        var face = new Grid();
        if (IsImage(file.Name))
            picture.Background = new ImageBrush { ImageSource = new BitmapImage(file.Url) { DecodePixelWidth = 260 }, Stretch = Stretch.UniformToFill };
        else
        {
            var glyph = Ui.Icon(Ui.DocumentGlyph, 36, Ui.Secondary);
            var icon = new Image { Width = 48, Height = 48 };
            face.Children.Add(glyph);
            face.Children.Add(icon);
            _ = Show(icon, glyph, file.Name);
        }
        if (model.Opening.Contains(file.FileId))  // downloading, to show: the first time, it takes a moment
            face.Children.Add(new Border
            {
                Width = 32, Height = 32, CornerRadius = new CornerRadius(16), Background = Ui.Theme("AcrylicInAppFillColorDefaultBrush"),
                Child = new ProgressRing { IsActive = true, Width = 18, Height = 18 },
            });
        picture.Child = face;
        var name = Ui.Text(file.Name).With(t => { t.MaxLines = 2; t.TextAlignment = TextAlignment.Center; t.TextTrimming = TextTrimming.CharacterEllipsis; });
        var added = file.AddedAt.ToLocalTime().ToString("d MMM yyyy", System.Globalization.CultureInfo.CurrentCulture);
        var tile = Ui.Column(6, picture, name, Ui.Caption($"{Size(file.Size)} · {added}").With(c => c.HorizontalAlignment = HorizontalAlignment.Center));
        tile.Width = 130;
        tile.Margin = new Thickness(4, 6, 4, 6);
        var menu = new MenuFlyout();
        menu.Items.Add(Ui.Item("Open", () => _ = Open(file), glyph: Ui.OpenGlyph));
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(Ui.Item("Delete…", () => _ = Delete(file), glyph: Ui.DeleteGlyph).With(i => i.KeyboardAcceleratorTextOverride = "Delete"));
        var item = new GridViewItem { Content = tile, Tag = file.FileId, ContextFlyout = menu };
        ToolTipService.SetToolTip(item, file.Name);
        Ui.Name(item, $"{file.Name}, {Size(file.Size)}, added {added}");
        return item;
    }

    private static bool IsImage(string name) => Path.GetExtension(name).ToLowerInvariant() is ".png" or ".jpg" or ".jpeg" or ".gif" or ".bmp" or ".webp";

    /// <summary>"70 KB", "1.2 MB": decimal units, as the Mac app writes sizes.</summary>
    private static string Size(long bytes) => bytes switch
    {
        < 1000 => $"{bytes} bytes",
        < 999_500 => $"{bytes / 1000.0:0} KB",  // the band by what it rounds to: never "1000 KB"
        < 999_950_000 => $"{bytes / 1e6:0.#} MB",
        _ => $"{bytes / 1e9:0.#} GB",
    };

    /// <summary>The kind's own icon, from the shell: asked of an empty file of that kind, once.</summary>
    private async Task Show(Image icon, FontIcon glyph, string name)
    {
        var kind = Path.GetExtension(name).ToLowerInvariant() is { Length: > 1 } extension ? extension : ".bin";
        if (!Kinds.TryGetValue(kind, out var load)) Kinds[kind] = load = Load(Path.Combine(model.Platform.CacheFolder, "kinds"), kind);
        if (await load is not { } source) return;
        icon.Source = source;
        glyph.Visibility = Visibility.Collapsed;

        static async Task<ImageSource?> Load(string folder, string kind)
        {
            try
            {
                Directory.CreateDirectory(folder);
                var path = Path.Combine(folder, "file" + kind);
                if (!File.Exists(path)) await File.WriteAllBytesAsync(path, []);
                var file = await StorageFile.GetFileFromPathAsync(path);
                using var thumbnail = await file.GetThumbnailAsync(ThumbnailMode.SingleItem, 96, ThumbnailOptions.UseCurrentScale);
                var bitmap = new BitmapImage();
                await bitmap.SetSourceAsync(thumbnail);
                return bitmap;
            }
            catch (Exception)
            {
                return null;  // the document glyph stays
            }
        }
    }

    /// <summary>Opens it in its app, downloading it the first time. Windows has no Quick Look, so a look is opening it.</summary>
    private async Task Open(MeetingFile file)
    {
        if (await model.Local(file, meeting) is { } path) model.Platform.Shell.Open(path);
    }

    private async Task Delete(MeetingFile file)
    {
        if (await Dialogs.Confirm($"Delete “{file.Name}”?", "It's deleted from this meeting. This can't be undone.", "Delete"))
            await model.Delete(file, meeting);
    }

    private async Task Choose()
    {
        var picker = new FileOpenPicker { ViewMode = PickerViewMode.List, CommitButtonText = "Add" };
        picker.FileTypeFilter.Add("*");
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(window));
        var chosen = await picker.PickMultipleFilesAsync();
        if (chosen.Count > 0) await model.AddFiles(chosen.Select(f => f.Path).ToList(), meeting);
    }

    /// <summary>Ctrl+V: files copied in File Explorer, or an image on the clipboard as a PNG.</summary>
    public async Task Paste()
    {
        IReadOnlyList<IStorageItem> copied = [];
        try
        {
            var content = Clipboard.GetContent();
            if (content.Contains(StandardDataFormats.StorageItems)) copied = await content.GetStorageItemsAsync();
        }
        catch (Exception)  // the clipboard held by another program, or files that can't be read yet (new Outlook)
        {
            model.Problem = new("Tally couldn't read what's on the clipboard. Copy it again (a file saved to your PC first), then paste.");
            return;
        }
        var paths = copied.OfType<StorageFile>().Select(f => f.Path).ToList();  // not folders
        if (paths.Count > 0) await model.AddFiles(paths, meeting);
        else await model.PasteImage(meeting);
    }
}
