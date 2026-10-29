using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;

namespace Tally;

/// <summary>
/// Children side by side, wrapping onto the next line as words do: who was there, the folders line, what's set aside.
/// WinUI has no wrap panel of its own.
/// </summary>
internal sealed class Flow : Microsoft.UI.Xaml.Controls.Panel
{
    private const double LineSpacing = 6;

    public double Spacing { get; init; } = 6;

    protected override Size MeasureOverride(Size available)
    {
        double x = 0, y = 0, line = 0, widest = 0;
        foreach (var child in Children)
        {
            child.Measure(new Size(available.Width, double.PositiveInfinity));
            var size = child.DesiredSize;
            if (x > 0 && x + size.Width > available.Width)
            {
                y += line + LineSpacing;
                x = line = 0;
            }
            x += size.Width + Spacing;
            line = Math.Max(line, size.Height);
            widest = Math.Max(widest, x - Spacing);
        }
        return new Size(double.IsInfinity(available.Width) ? widest : Math.Min(widest, available.Width), y + line);
    }

    protected override Size ArrangeOverride(Size final)
    {
        double x = 0, y = 0, line = 0;
        foreach (var child in Children)
        {
            var size = child.DesiredSize;
            if (x > 0 && x + size.Width > final.Width)
            {
                y += line + LineSpacing;
                x = line = 0;
            }
            child.Arrange(new Rect(x, y, Math.Min(size.Width, final.Width), size.Height));
            x += size.Width + Spacing;
            line = Math.Max(line, size.Height);
        }
        return final;
    }
}

/// <summary>
/// A part of a page drawn again only when what it shows has changed, said as a signature: a sync brings every list
/// anew every minute, and redrawing all of it would close an open flyout and lose what's typed in it. Focus inside
/// it stays inside it, or goes to the page, not on to whatever field is next (which would look like it's being edited).
/// </summary>
internal sealed class Section : Grid
{
    private string? drawn;

    public void Show(string signature, Func<UIElement?> build)
    {
        if (signature == drawn) return;
        drawn = signature;
        var focused = XamlRoot is { } root && FocusManager.GetFocusedElement(root) is DependencyObject element && Ui.Within(element, this);
        Children.Clear();
        if (build() is { } child) Children.Add(child);
        Visibility = Children.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        if (!focused) return;
        if (Children.Count > 0 && FocusManager.FindFirstFocusableElement(Children[0]) is Control first) first.Focus(FocusState.Programmatic);
        else for (DependencyObject? at = this; at != null; at = VisualTreeHelper.GetParent(at)) if (at is ScrollViewer page) { page.Focus(FocusState.Programmatic); break; }
    }
}

/// <summary>
/// As many columns of at least <see cref="MinColumnWidth"/> as fit, each child the column's width: the speakers to
/// confirm, two side by side when there's room.
/// </summary>
internal sealed class Columns : Microsoft.UI.Xaml.Controls.Panel
{
    private const double MinColumnWidth = 260;
    private const double Spacing = 10;

    private (int Count, double Width) Fit(double width)
    {
        if (double.IsInfinity(width)) return (1, MinColumnWidth);
        var count = Math.Max(1, (int)((width + Spacing) / (MinColumnWidth + Spacing)));
        count = Math.Min(count, Math.Max(1, Children.Count));
        return (count, (width - Spacing * (count - 1)) / count);
    }

    protected override Size MeasureOverride(Size available)
    {
        var (count, width) = Fit(available.Width);
        double height = 0, row = 0;
        for (var i = 0; i < Children.Count; i++)
        {
            Children[i].Measure(new Size(width, double.PositiveInfinity));
            row = Math.Max(row, Children[i].DesiredSize.Height);
            if (i % count == count - 1 || i == Children.Count - 1)
            {
                height += row + (i == Children.Count - 1 ? 0 : Spacing);
                row = 0;
            }
        }
        return new Size(double.IsInfinity(available.Width) ? width : available.Width, height);
    }

    protected override Size ArrangeOverride(Size final)
    {
        var (count, width) = Fit(final.Width);
        double y = 0;
        for (var start = 0; start < Children.Count; start += count)
        {
            var row = Enumerable.Range(start, Math.Min(count, Children.Count - start)).Max(i => Children[i].DesiredSize.Height);
            for (var i = start; i < Math.Min(start + count, Children.Count); i++)
                Children[i].Arrange(new Rect((i - start) * (width + Spacing), y, width, row));
            y += row + Spacing;
        }
        return final;
    }
}
