using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using ContextBridge.App.Resources;

namespace ContextBridge.App.Views;

/// <summary>Prompt, list picker and labeled-choice dialogs, all built on <see cref="AppDialog"/>.</summary>
static class InputDialog
{
    public static string? Show(Window? owner, string title, string label, string initial)
    {
        var box = new TextBox { Text = initial, Margin = new Thickness(0, 14, 0, 0), Padding = new Thickness(8, 5, 8, 5) };
        box.GotKeyboardFocus += (_, _) => box.SelectAll();
        int r = AppDialog.Run(owner ?? AppDialog.ActiveWindow(), title, MessageBoxImage.None, label, null, box,
                              new[] { Strings.Common_OK, Strings.Common_Cancel }, defaultIndex: 0, cancelIndex: 1,
                              canAccept: () => !string.IsNullOrWhiteSpace(box.Text), focus: box);
        return r == 0 ? box.Text : null;
    }

    public static int Choose(Window? owner, string title, string label, IReadOnlyList<string> options)
    {
        // Large, clearly separated choices (radio-style list) instead of a bare ListBox.
        var list = new ListBox
        {
            ItemsSource = options,
            SelectedIndex = 0,
            Margin = new Thickness(0, 14, 0, 0),
            BorderThickness = new Thickness(0),
        };
        list.ItemContainerStyle = new Style(typeof(ListBoxItem), Application.Current?.TryFindResource(typeof(ListBoxItem)) as Style)
        {
            Setters = { new Setter(Control.PaddingProperty, new Thickness(12, 8, 12, 8)) },
        };
        int result = -1;
        Window? dialogWindow = null;
        list.MouseDoubleClick += (_, e) =>
        {
            if (list.SelectedIndex < 0 || e.OriginalSource is not FrameworkElement { DataContext: string }) return;
            result = list.SelectedIndex;
            dialogWindow = Window.GetWindow(list);
            if (dialogWindow is not null) dialogWindow.DialogResult = true;
        };
        list.Loaded += (_, _) => (list.ItemContainerGenerator.ContainerFromIndex(0) as ListBoxItem)?.Focus();
        list.PreviewKeyDown += (_, e) =>
        {
            if (e.Key != Key.Enter || list.SelectedIndex < 0) return;
            result = list.SelectedIndex;
            var w = Window.GetWindow(list);
            if (w is not null) w.DialogResult = true;
            e.Handled = true;
        };
        int r = AppDialog.Run(owner ?? AppDialog.ActiveWindow(), title, MessageBoxImage.None, label, null, list,
                              new[] { Strings.Common_Select, Strings.Common_Cancel }, defaultIndex: 0, cancelIndex: 1,
                              canAccept: () => list.SelectedIndex >= 0, focus: list);
        if (result >= 0) return result;
        return r == 0 ? list.SelectedIndex : -1;
    }

    /// <summary>
    /// A question with explicitly labeled choices instead of Yes/No. Returns the index of the chosen
    /// button, or -1 when cancelled.
    /// </summary>
    public static int Ask(Window? owner, string title, string message, string? note, IReadOnlyList<string> choices,
                          int defaultIndex = 0, MessageBoxImage image = MessageBoxImage.Question,
                          string? cancelLabel = null)
    {
        var buttons = choices.Append(cancelLabel ?? Strings.Common_Cancel).ToList();
        string heading = message, body = note ?? "";
        int split = message.Replace("\r\n", "\n").IndexOf("\n\n", StringComparison.Ordinal);
        if (split >= 0)
        {
            var m = message.Replace("\r\n", "\n");
            heading = m[..split].Trim();
            body = (m[(split + 2)..].Trim() + (note is null ? "" : "\n\n" + note)).Trim();
        }
        int r = AppDialog.Run(owner ?? AppDialog.ActiveWindow(), title, image, heading, body, null, buttons,
                              defaultIndex, cancelIndex: buttons.Count - 1);
        return r >= 0 && r < choices.Count ? r : -1;
    }
}
