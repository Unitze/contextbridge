using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using ContextBridge.App.Resources;

namespace ContextBridge.App.Views;

/// <summary>
/// Themed replacement for <see cref="MessageBox"/> in the style of a Windows 11 content dialog:
/// optional status icon, bold heading, body text, and a footer with right-aligned buttons whose
/// primary action uses the accent color. Unlike MessageBox it follows the Fluent light/dark theme
/// and uses the app's language for its button captions.
/// </summary>
static class AppDialog
{
    // ---- MessageBox-compatible API ----

    public static MessageBoxResult Show(string text) => Show(text, "ContextBridge");

    public static MessageBoxResult Show(string text, string caption) =>
        Show(text, caption, MessageBoxButton.OK, MessageBoxImage.None);

    public static MessageBoxResult Show(string text, string caption, MessageBoxButton buttons) =>
        Show(text, caption, buttons, MessageBoxImage.None);

    public static MessageBoxResult Show(string text, string caption, MessageBoxButton buttons, MessageBoxImage image) =>
        Show(text, caption, buttons, image, MessageBoxResult.None);

    public static MessageBoxResult Show(string text, string caption, MessageBoxButton buttons, MessageBoxImage image,
                                        MessageBoxResult defaultResult, IReadOnlyList<string>? labels = null)
    {
        var results = buttons switch
        {
            MessageBoxButton.OKCancel => new[] { MessageBoxResult.OK, MessageBoxResult.Cancel },
            MessageBoxButton.YesNo => new[] { MessageBoxResult.Yes, MessageBoxResult.No },
            MessageBoxButton.YesNoCancel => new[] { MessageBoxResult.Yes, MessageBoxResult.No, MessageBoxResult.Cancel },
            _ => new[] { MessageBoxResult.OK },
        };
        var captions = results.Select((r, i) => labels is not null && i < labels.Count ? labels[i] : Caption(r)).ToList();
        int defaultIndex = Math.Max(0, Array.IndexOf(results, defaultResult));
        // Cancel (or the only button) closes on Esc; for Yes/No, Esc means No.
        int cancelIndex = Array.IndexOf(results, MessageBoxResult.Cancel);
        if (cancelIndex < 0) cancelIndex = results.Length == 1 ? 0 : Array.IndexOf(results, MessageBoxResult.No);

        var (heading, body) = SplitHeading(text);
        int chosen = Run(ActiveWindow(), caption, image, heading, body, extra: null, captions, defaultIndex, cancelIndex);
        if (chosen < 0)
            return buttons == MessageBoxButton.YesNo ? MessageBoxResult.No
                 : buttons == MessageBoxButton.OK ? MessageBoxResult.OK
                 : MessageBoxResult.Cancel;
        return results[chosen];
    }

    // ---- building blocks shared with InputDialog ----

    /// <summary>
    /// Shows the dialog and returns the index of the clicked button, or -1 when it was closed
    /// with Esc / the close button and there is no cancel button.
    /// </summary>
    internal static int Run(Window? owner, string title, MessageBoxImage image, string heading, string? body,
                            UIElement? extra, IReadOnlyList<string> buttons, int defaultIndex, int cancelIndex,
                            Func<bool>? canAccept = null, UIElement? focus = null)
    {
        var content = new DockPanel { LastChildFill = true };

        // Footer
        var footer = new Border
        {
            Padding = new Thickness(24, 16, 24, 16),
            BorderThickness = new Thickness(0, 1, 0, 0),
        };
        footer.SetResourceReference(Border.BackgroundProperty, "SolidBackgroundFillColorBaseBrush");
        footer.SetResourceReference(Border.BorderBrushProperty, "CardStrokeColorDefaultBrush");
        var row = new UniformGrid(buttons.Count);
        footer.Child = row;
        DockPanel.SetDock(footer, Dock.Bottom);
        content.Children.Add(footer);

        // Body
        var main = new Grid { Margin = new Thickness(24, 22, 24, 22) };
        main.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        main.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        if (Glyph(image) is { } glyph)
        {
            var icon = new TextBlock
            {
                Text = glyph.Text,
                FontSize = 28,
                Margin = new Thickness(0, 0, 16, 0),
                VerticalAlignment = VerticalAlignment.Top,
            };
            icon.SetResourceReference(TextBlock.FontFamilyProperty, "IconFont");
            icon.SetResourceReference(TextBlock.ForegroundProperty, glyph.Brush);
            main.Children.Add(icon);
        }
        var stack = new StackPanel();
        Grid.SetColumn(stack, 1);
        main.Children.Add(stack);
        var head = new TextBlock
        {
            Text = heading,
            FontSize = 18,
            FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap,
        };
        head.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorPrimaryBrush");
        stack.Children.Add(head);
        if (!string.IsNullOrWhiteSpace(body))
        {
            // Read-only TextBox so error details can be selected and copied.
            var detail = new TextBox
            {
                Text = body,
                IsReadOnly = true,
                TextWrapping = TextWrapping.Wrap,
                BorderThickness = new Thickness(0),
                Background = Brushes.Transparent,
                Padding = new Thickness(0),
                Margin = new Thickness(0, 8, 0, 0),
                MaxHeight = 320,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Focusable = true,
                IsTabStop = false,
            };
            detail.SetResourceReference(TextBox.ForegroundProperty, "TextFillColorSecondaryBrush");
            stack.Children.Add(detail);
        }
        if (extra is not null)
        {
            if (extra is FrameworkElement fe && fe.Margin == default) fe.Margin = new Thickness(0, 14, 0, 0);
            stack.Children.Add(extra);
        }
        content.Children.Add(main);

        var win = new Window
        {
            Title = title,
            Content = content,
            Owner = owner,
            Width = buttons.Sum(b => b.Length) > 24 ? 560 : 480,
            MinWidth = 360,
            MaxWidth = 640,
            SizeToContent = SizeToContent.Height,
            ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = owner is null ? WindowStartupLocation.CenterScreen : WindowStartupLocation.CenterOwner,
            ShowInTaskbar = owner is null,
        };
        if (Application.Current?.MainWindow?.Icon is { } appIcon) win.Icon = appIcon;

        int result = -1;
        var accept = new List<Button>();
        for (int i = 0; i < buttons.Count; i++)
        {
            int index = i;
            var b = new Button
            {
                // Wrapping text so long, explicit choices ("Delete group and commands") still fit.
                Content = new TextBlock { Text = buttons[i], TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center },
                MinWidth = 96,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                Padding = new Thickness(16, 6, 16, 6),
                Margin = new Thickness(i == 0 ? 0 : 8, 0, 0, 0),
                IsDefault = i == defaultIndex,
                IsCancel = i == cancelIndex,
            };
            if (i == defaultIndex) b.SetResourceReference(FrameworkElement.StyleProperty, "AccentButtonStyle");
            if (i != cancelIndex) accept.Add(b);
            b.Click += (_, _) =>
            {
                if (index != cancelIndex && canAccept is not null && !canAccept()) return;
                result = index;
                win.DialogResult = true;
            };
            row.Children.Add(b);
        }
        // Buttons share the width evenly and hug the right edge, like a Windows 11 dialog.
        row.HorizontalAlignment = buttons.Count == 1 ? HorizontalAlignment.Right : HorizontalAlignment.Stretch;
        if (buttons.Count == 1) row.MinWidth = 120;

        win.Loaded += (_, _) =>
        {
            if (focus is not null) focus.Focus();
            else if (defaultIndex >= 0 && defaultIndex < row.Children.Count) ((Button)row.Children[defaultIndex]).Focus();
        };
        win.PreviewKeyDown += (_, e) =>
        {
            // Ctrl+C copies the whole message, like MessageBox does.
            if (e.Key == Key.C && Keyboard.Modifiers == ModifierKeys.Control && Keyboard.FocusedElement is not TextBox)
            {
                try { Clipboard.SetText(string.IsNullOrEmpty(body) ? heading : heading + Environment.NewLine + Environment.NewLine + body); }
                catch (Exception) { /* clipboard busy */ }
                e.Handled = true;
            }
        };
        win.ShowDialog();
        return result >= 0 ? result : cancelIndex;
    }

    /// <summary>The first paragraph becomes the heading; the rest is shown as detail text.</summary>
    static (string Heading, string? Body) SplitHeading(string text)
    {
        text = text.Replace("\r\n", "\n").Trim();
        int split = text.IndexOf("\n\n", StringComparison.Ordinal);
        if (split < 0) return (text, null);
        return (text[..split].Trim(), text[(split + 2)..].Trim());
    }

    static (string Text, string Brush)? Glyph(MessageBoxImage image) => image switch
    {
        MessageBoxImage.Error => ("", "SystemFillColorCriticalBrush"),
        MessageBoxImage.Warning => ("", "SystemFillColorCautionBrush"),
        MessageBoxImage.Question => ("", "AccentTextFillColorPrimaryBrush"),
        MessageBoxImage.Information => ("", "AccentTextFillColorPrimaryBrush"),
        _ => null,
    };

    static string Caption(MessageBoxResult r) => r switch
    {
        MessageBoxResult.Yes => Strings.Common_Yes,
        MessageBoxResult.No => Strings.Common_No,
        MessageBoxResult.Cancel => Strings.Common_Cancel,
        _ => Strings.Common_OK,
    };

    internal static Window? ActiveWindow()
    {
        var app = Application.Current;
        if (app is null) return null;
        return app.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive && w.IsVisible)
               ?? (app.MainWindow is { IsVisible: true } m ? m : null);
    }

    /// <summary>Panel that gives every child the same width (WPF's UniformGrid lives in Primitives).</summary>
    sealed class UniformGrid(int columns) : System.Windows.Controls.Primitives.UniformGrid
    {
        protected override Size MeasureOverride(Size constraint)
        {
            Columns = columns;
            Rows = 1;
            return base.MeasureOverride(constraint);
        }
    }
}
