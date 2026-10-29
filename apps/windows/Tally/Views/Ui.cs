using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace Tally;

/// <summary>
/// Small builders for views written in code, in Windows 11's own type ramp and colours (Segoe UI Variable, Segoe
/// Fluent Icons, the theme's brushes). Tally's views are drawn from the model as SwiftUI's are on the Mac: a view
/// builds its controls from what the model says now, and again when that changes.
/// </summary>
internal static class Ui
{
    public static readonly Color Red = Color.FromArgb(0xFF, 0xC4, 0x2B, 0x1C);  // Windows' own critical red
    public static readonly Color Orange = Color.FromArgb(0xFF, 0xF7, 0x63, 0x0C);
    public static readonly Color Green = Color.FromArgb(0xFF, 0x0F, 0x7B, 0x0F);

    public static Brush Theme(string key) => (Brush)Application.Current.Resources[key];
    public static Style Style(string key) => (Style)Application.Current.Resources[key];
    public static Brush Secondary => Theme("TextFillColorSecondaryBrush");

    public static TextBlock Text(string text, string style = "BodyTextBlockStyle", Brush? brush = null, bool wrap = true)
    {
        var block = new TextBlock { Text = text, Style = Style(style), TextWrapping = wrap ? TextWrapping.Wrap : TextWrapping.NoWrap };
        if (!wrap) block.TextTrimming = TextTrimming.CharacterEllipsis;
        if (brush is not null) block.Foreground = brush;
        return block;
    }

    public static TextBlock Caption(string text, Brush? brush = null) => Text(text, "CaptionTextBlockStyle", brush ?? Secondary);

    public static FontIcon Icon(string glyph, double size = 16, Brush? brush = null)
    {
        var icon = new FontIcon { Glyph = glyph, FontSize = size };
        if (brush is not null) icon.Foreground = brush;
        return icon;
    }

    public static StackPanel Column(double spacing, params UIElement?[] children) => Stack(Orientation.Vertical, spacing, children);
    public static StackPanel Row(double spacing, params UIElement?[] children) => Stack(Orientation.Horizontal, spacing, children);

    private static StackPanel Stack(Orientation orientation, double spacing, UIElement?[] children)
    {
        var stack = new StackPanel { Orientation = orientation, Spacing = spacing };
        foreach (var child in children) if (child is not null) stack.Children.Add(child);
        return stack;
    }

    /// <summary>A button with its text, after a glyph if it has one. A <paramref name="label"/> is shown in place of the
    /// text, for the caller to change (the time on Stop); screen readers still read the text.</summary>
    public static Button Button(string text, Action click, bool accent = false, string? glyph = null, TextBlock? label = null)
    {
        var button = new Button { Content = glyph is null ? text : Row(8, Icon(glyph, 14), label ?? new TextBlock { Text = text }) };
        if (accent) button.Style = Style("AccentButtonStyle");
        button.Click += (_, _) => click();
        Name(button, text);
        return button;
    }

    /// <summary>A button in Tally's red: Start Recording, as the Mac's red button.</summary>
    public static Button RedButton(string text, string glyph, Action click, TextBlock? label = null)
    {
        var button = Button(text, click, glyph: glyph, label: label);
        button.Background = new SolidColorBrush(Red);
        button.Foreground = new SolidColorBrush(Colors.White);
        button.Resources["ButtonBackgroundPointerOver"] = new SolidColorBrush(Color.FromArgb(0xFF, 0xB0, 0x26, 0x19));
        button.Resources["ButtonBackgroundPressed"] = new SolidColorBrush(Color.FromArgb(0xFF, 0x9C, 0x22, 0x16));
        button.Resources["ButtonForegroundPointerOver"] = new SolidColorBrush(Colors.White);
        button.Resources["ButtonForegroundPressed"] = new SolidColorBrush(Color.FromArgb(0xCC, 0xFF, 0xFF, 0xFF));
        button.Resources["ButtonBorderBrushPointerOver"] = new SolidColorBrush(Colors.Transparent);
        return button;
    }

    /// <summary>A plain text button, as a link in a dialog: for the quieter actions.</summary>
    public static HyperlinkButton Link(string text, Action click)
    {
        var link = new HyperlinkButton { Content = text, Padding = new Thickness(4, 2, 4, 2) };
        link.Click += (_, _) => click();
        return link;
    }

    public static Border Divider() => new() { Height = 1, Background = Theme("DividerStrokeColorDefaultBrush"), Margin = new Thickness(0, 2, 0, 2) };

    public static Border Badge(int count, string label)
    {
        var badge = new Border
        {
            Background = new SolidColorBrush(Orange),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(6, 0, 6, 1),
            VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock { Text = count.ToString(), FontSize = 11, FontWeight = FontWeights.SemiBold, Foreground = new SolidColorBrush(Colors.White) },
        };
        ToolTipService.SetToolTip(badge, label);
        Name(badge, label);
        return badge;
    }

    public static T With<T>(this T element, Action<T> set) where T : DependencyObject
    {
        set(element);
        return element;
    }

    public static T At<T>(this T element, int column, int row = 0) where T : FrameworkElement
    {
        Microsoft.UI.Xaml.Controls.Grid.SetColumn(element, column);
        Microsoft.UI.Xaml.Controls.Grid.SetRow(element, row);
        return element;
    }

    /// <summary>A grid of these columns ("Auto,*,Auto"), and of these rows if given.</summary>
    public static Grid Grid(string columns, string? rows = null, double spacing = 0)
    {
        var grid = new Grid { ColumnSpacing = spacing };
        foreach (var c in columns.Split(',')) grid.ColumnDefinitions.Add(new ColumnDefinition { Width = Length(c) });
        foreach (var r in rows?.Split(',') ?? []) grid.RowDefinitions.Add(new RowDefinition { Height = Length(r) });
        return grid;

        static GridLength Length(string s) => s == "Auto" ? GridLength.Auto
            : s.EndsWith('*') ? new GridLength(s.Length == 1 ? 1 : double.Parse(s[..^1]), GridUnitType.Star) : new GridLength(double.Parse(s));
    }

    public static Grid Add(this Grid grid, params UIElement?[] children)
    {
        foreach (var child in children) if (child is not null) grid.Children.Add(child);
        return grid;
    }

    public static void Name(UIElement element, string name) => AutomationProperties.SetName(element, name);

    /// <summary>A heading under the page's own, for screen readers too.</summary>
    public static TextBlock Heading(string text)
    {
        var heading = Text(text, "BodyLargeTextBlockStyle").With(b => b.FontWeight = FontWeights.SemiBold);
        AutomationProperties.SetHeadingLevel(heading, AutomationHeadingLevel.Level2);
        return heading;
    }

    /// <summary>Whether element is ancestor or inside it.</summary>
    public static bool Within(DependencyObject? element, DependencyObject ancestor)
    {
        for (var at = element; at != null; at = VisualTreeHelper.GetParent(at)) if (at == ancestor) return true;
        return false;
    }

    /// <summary>What a page shows when there's nothing to show yet, as Windows' own empty states: a large icon, a title,
    /// a sentence, and what to do.</summary>
    public static StackPanel Placeholder(string glyph, string title, string? description, params UIElement?[] actions)
    {
        var stack = Column(10,
            Icon(glyph, 40, Secondary).With(i => i.Margin = new Thickness(0, 0, 0, 6)),
            Text(title, "SubtitleTextBlockStyle").With(b => { b.TextAlignment = TextAlignment.Center; AutomationProperties.SetHeadingLevel(b, AutomationHeadingLevel.Level1); }),
            description is null ? null : Text(description, brush: Secondary).With(b => { b.TextAlignment = TextAlignment.Center; b.MaxWidth = 380; }));
        var buttons = Row(8, actions);
        if (buttons.Children.Count > 0)
        {
            buttons.HorizontalAlignment = HorizontalAlignment.Center;
            buttons.Margin = new Thickness(0, 8, 0, 0);
            stack.Children.Add(buttons);
        }
        stack.HorizontalAlignment = HorizontalAlignment.Center;
        stack.VerticalAlignment = VerticalAlignment.Center;
        stack.Padding = new Thickness(24);
        foreach (var child in stack.Children) if (child is FrameworkElement e && e != buttons) e.HorizontalAlignment = HorizontalAlignment.Center;
        return stack;
    }

    /// <summary>Someone's initials in a circle, as People shows a person without a photo.</summary>
    public static PersonPicture Initials(string name, double size) => new PersonPicture
    {
        DisplayName = name, Width = size, Height = size, IsTabStop = false,
    }.With(p => AutomationProperties.SetAccessibilityView(p, AccessibilityView.Raw));

    /// <summary>The owner's own person, wherever people are listed.</summary>
    public static Border YouBadge() => new()
    {
        Background = Tint(Accent, 0x30),
        CornerRadius = new CornerRadius(4),
        Padding = new Thickness(5, 0, 5, 1),
        VerticalAlignment = VerticalAlignment.Center,
        Child = new TextBlock { Text = "You", FontSize = 11, FontWeight = FontWeights.SemiBold, Foreground = Theme("AccentTextFillColorPrimaryBrush") },
    };

    /// <summary>A voice the owner has confirmed.</summary>
    public static FontIcon Seal(double size = 14)
    {
        var seal = Icon(SealGlyph, size, Success);
        seal.VerticalAlignment = VerticalAlignment.Center;
        ToolTipService.SetToolTip(seal, "Voice confirmed");
        Name(seal, "Voice confirmed");
        return seal;
    }

    /// <summary>A quiet button, no frame until the pointer's over it: Add under a list, an action item's owner and due date.</summary>
    public static Button Subtle(string text, string glyph, Action click, string? name = null, TextBlock? label = null)
    {
        var button = new Button
        {
            Content = Row(6, Icon(glyph, 12), label ?? new TextBlock { Text = text }),
            Padding = new Thickness(6, 3, 8, 4),
            Style = Style("SubtleButtonStyle"),
            Foreground = Secondary,
        };
        button.Click += (_, _) => click();
        Name(button, name ?? text);
        return button;
    }

    /// <summary>A menu filled as it opens, so it shows things as they are then. It holds a placeholder until then:
    /// an empty menu wouldn't open to be filled.</summary>
    public static MenuFlyout Menu(Action<IList<MenuFlyoutItemBase>> fill)
    {
        var menu = new MenuFlyout();
        menu.Items.Add(new MenuFlyoutItem { Text = "…" });
        menu.Opening += (_, _) =>
        {
            menu.Items.Clear();
            fill(menu.Items);
        };
        return menu;
    }

    public static MenuFlyoutItem Item(string text, Action click, bool enabled = true, string? glyph = null)
    {
        var item = new MenuFlyoutItem { Text = text, IsEnabled = enabled };
        if (glyph is not null) item.Icon = new FontIcon { Glyph = glyph };
        item.Click += (_, _) => click();
        return item;
    }

    /// <summary>Windows' own green for what's done and confirmed, readable in light and dark.</summary>
    public static Brush Success => Theme("SystemFillColorSuccessBrush");

    /// <summary>Tally's orange as text, readable on the theme's background: darker on light, lighter on dark.</summary>
    public static Brush OrangeText => new SolidColorBrush(Application.Current.RequestedTheme == ApplicationTheme.Dark
        ? Color.FromArgb(0xFF, 0xFF, 0x8C, 0x42) : Color.FromArgb(0xFF, 0xB3, 0x47, 0x00));

    /// <summary>The system's accent colour, for tints.</summary>
    public static Color Accent => (Color)Application.Current.Resources["SystemAccentColor"];

    /// <summary>A colour seen through: a highlight, a box's fill.</summary>
    public static SolidColorBrush Tint(Color color, byte alpha) => new(Color.FromArgb(alpha, color.R, color.G, color.B));

    /// <summary>A rounded card, as Windows 11 draws one on its layer.</summary>
    public static Border Card(UIElement child) => new()
    {
        Child = child,
        Padding = new Thickness(12),
        CornerRadius = new CornerRadius(8),
        Background = Theme("CardBackgroundFillColorDefaultBrush"),
        BorderBrush = Theme("CardStrokeColorDefaultBrush"),
        BorderThickness = new Thickness(1),
    };

    // Segoe Fluent Icons
    public const string RecordGlyph = "\uE7C8", StopGlyph = "\uE71A", DocumentGlyph = "\uE8A5", WarningGlyph = "\uE7BA",
        OfflineGlyph = "\uF384", PersonGlyph = "\uE77B", DownloadGlyph = "\uE896", FolderGlyph = "\uE8B7", ErrorGlyph = "\uE783",
        DotGlyph = "\uE91F", CheckGlyph = "\uE73E", SyncGlyph = "\uE895",
        AddGlyph = "\uE710", DeleteGlyph = "\uE74D", EditGlyph = "\uE70F", CopyGlyph = "\uE8C8", MoreGlyph = "\uE712", PlayGlyph = "\uE768",
        PauseGlyph = "\uE769", PeopleGlyph = "\uE716", CalendarGlyph = "\uE787", SortGlyph = "\uE8CB", NewFolderGlyph = "\uE8F4",
        SealGlyph = "\uEC61", SearchGlyph = "\uE721", MeetingsGlyph = "\uE8F2", WaveGlyph = "\uE9D9", CloseGlyph = "\uE711",
        VolumeOffGlyph = "\uE74F", OpenGlyph = "\uE8E5", CloudGlyph = "\uE753",
        VolumeGlyph = "\uE767", NoteGlyph = "\uF000", ChevronGlyph = "\uE70D";
}
