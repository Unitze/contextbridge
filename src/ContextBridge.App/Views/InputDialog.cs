using System.Windows;
using System.Windows.Controls;
using ContextBridge.App.Resources;

namespace ContextBridge.App.Views;

/// <summary>Small code-built dialogs (WPF has no prompt, list picker or task dialog of its own).</summary>
static class InputDialog
{
    static Window Create(Window? owner, string title, UIElement content) => new()
    {
        Title = title,
        Content = content,
        Owner = owner,
        SizeToContent = SizeToContent.WidthAndHeight,
        ResizeMode = ResizeMode.NoResize,
        WindowStartupLocation = owner is null ? WindowStartupLocation.CenterScreen : WindowStartupLocation.CenterOwner,
        ShowInTaskbar = false,
    };

    static Button MakeButton(string text, bool isDefault = false, bool isCancel = false)
    {
        var b = new Button
        {
            Content = text,
            IsDefault = isDefault,
            IsCancel = isCancel,
            MinWidth = 88,
            Padding = new Thickness(16, 5, 16, 5),
            Margin = new Thickness(8, 0, 0, 0),
        };
        if (isDefault && Application.Current?.TryFindResource("AccentButtonStyle") is Style accent) b.Style = accent;
        return b;
    }

    static StackPanel ButtonRow(params Button[] buttons)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        foreach (var b in buttons) row.Children.Add(b);
        return row;
    }

    public static string? Show(Window? owner, string title, string label, string initial)
    {
        var box = new TextBox { Text = initial, MinWidth = 340, Margin = new Thickness(0, 6, 0, 16) };
        var ok = MakeButton(Strings.Common_OK, isDefault: true);
        var panel = new StackPanel { Margin = new Thickness(20) };
        panel.Children.Add(new Label { Content = label, Target = box, Padding = new Thickness(0) });
        panel.Children.Add(box);
        panel.Children.Add(ButtonRow(ok, MakeButton(Strings.Common_Cancel, isCancel: true)));

        var win = Create(owner, title, panel);
        ok.Click += (_, _) => win.DialogResult = true;
        win.Loaded += (_, _) => { box.Focus(); box.SelectAll(); };
        return win.ShowDialog() == true ? box.Text : null;
    }

    public static int Choose(Window? owner, string title, string label, IReadOnlyList<string> options)
    {
        var list = new ListBox { ItemsSource = options, SelectedIndex = 0, MinWidth = 320, Margin = new Thickness(0, 6, 0, 16) };
        var ok = MakeButton(Strings.Common_Select, isDefault: true);
        var panel = new StackPanel { Margin = new Thickness(20) };
        panel.Children.Add(new TextBlock { Text = label });
        panel.Children.Add(list);
        panel.Children.Add(ButtonRow(ok, MakeButton(Strings.Common_Cancel, isCancel: true)));

        var win = Create(owner, title, panel);
        ok.Click += (_, _) => win.DialogResult = true;
        list.MouseDoubleClick += (_, _) => { if (list.SelectedIndex >= 0) win.DialogResult = true; };
        win.Loaded += (_, _) => (list.ItemContainerGenerator.ContainerFromIndex(0) as ListBoxItem)?.Focus();
        return win.ShowDialog() == true ? list.SelectedIndex : -1;
    }

    /// <summary>
    /// A message with explicitly labeled choices instead of Yes/No (whose meaning would have to be
    /// explained in the text, and whose captions follow the Windows language, not the app's).
    /// Returns the index of the chosen button, or -1 when closed / cancelled.
    /// </summary>
    public static int Ask(Window? owner, string title, string message, string? note, IReadOnlyList<string> choices, int defaultIndex = 0)
    {
        var panel = new StackPanel { Margin = new Thickness(20), MaxWidth = 560 };
        panel.Children.Add(new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, note is null ? 20 : 8) });
        if (note is not null)
            panel.Children.Add(new TextBlock { Text = note, TextWrapping = TextWrapping.Wrap, Opacity = 0.7, FontSize = 12, Margin = new Thickness(0, 0, 0, 20) });

        int result = -1;
        Window? win = null;
        var buttons = new List<Button>();
        for (int i = 0; i < choices.Count; i++)
        {
            int index = i;
            var b = MakeButton(choices[i], isDefault: i == defaultIndex);
            b.Click += (_, _) => { result = index; win!.DialogResult = true; };
            buttons.Add(b);
        }
        buttons.Add(MakeButton(Strings.Common_Cancel, isCancel: true));
        panel.Children.Add(ButtonRow(buttons.ToArray()));

        win = Create(owner, title, panel);
        win.ShowDialog();
        return result;
    }
}
