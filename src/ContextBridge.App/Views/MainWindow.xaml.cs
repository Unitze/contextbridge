using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using ContextBridge.App.Services;
using ContextBridge.App.ViewModels;

namespace ContextBridge.App.Views;

partial class MainWindow : Window
{
    internal MainViewModel ViewModel { get; } = new();
    Point _dragStart;
    CommandViewModel? _dragItem;

    public MainWindow()
    {
        InitializeComponent();
        DataContext = ViewModel;
        Activated += (_, _) => ViewModel.RefreshPackageStatus();
    }

    public bool Initialize()
    {
        if (!ViewModel.Load()) return false;
        ViewModel.RefreshPackageStatus();
        RestoreLayout();
        return true;
    }

    // ---- window / layout persistence (app-settings.json) ----

    void RestoreLayout()
    {
        var prefs = AppPreferences.Current;
        if (prefs.Window is { Width: > 0, Height: > 0 } w)
        {
            // Only restore a position that is still (mostly) on a connected screen.
            var screen = new Rect(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop,
                                  SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
            var saved = new Rect(w.Left, w.Top, Math.Max(w.Width, MinWidth), Math.Max(w.Height, MinHeight));
            var visible = Rect.Intersect(screen, saved);
            if (!visible.IsEmpty && visible.Width >= 200 && visible.Height >= 120)
            {
                WindowStartupLocation = WindowStartupLocation.Manual;
                Left = saved.Left;
                Top = saved.Top;
                Width = saved.Width;
                Height = saved.Height;
            }
            if (w.Maximized) WindowState = WindowState.Maximized;
        }
        if (prefs.CommandListWidth >= ListColumn.MinWidth) ListColumn.Width = new GridLength(prefs.CommandListWidth);
        if (prefs.SelectedTab >= 0 && prefs.SelectedTab < Tabs.Items.Count) Tabs.SelectedIndex = prefs.SelectedTab;
    }

    void SaveLayout()
    {
        var prefs = AppPreferences.Current;
        var bounds = WindowState == WindowState.Normal ? new Rect(Left, Top, Width, Height) : RestoreBounds;
        if (!bounds.IsEmpty)
        {
            prefs.Window = new AppPreferences.WindowPlacement
            {
                Left = bounds.Left,
                Top = bounds.Top,
                Width = bounds.Width,
                Height = bounds.Height,
                Maximized = WindowState == WindowState.Maximized,
            };
        }
        if (ListColumn.ActualWidth > 0) prefs.CommandListWidth = ListColumn.ActualWidth;
        prefs.SelectedTab = Tabs.SelectedIndex;
        prefs.Save();
    }

    protected override void OnClosed(EventArgs e)
    {
        SaveLayout();
        base.OnClosed(e);
    }

    void SaveMenu_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { ContextMenu: { } menu } button) return;
        CommitPendingEdit();
        menu.PlacementTarget = button;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    void ClearSearch_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.SearchText = "";
        SearchBox.Focus();
    }

    /// <summary>Pushes a pending (delayed) edit of the focused text box into the view model.</summary>
    static void CommitPendingEdit()
    {
        if (Keyboard.FocusedElement is TextBox tb) tb.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        CommitPendingEdit();
        if (!ViewModel.ConfirmClose()) e.Cancel = true;
        base.OnClosing(e);
    }

    void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        // Ctrl+S right after typing: commit the delayed binding before the Save key binding runs.
        if (e.Key == Key.S && (Keyboard.Modifiers & ModifierKeys.Control) != 0) CommitPendingEdit();
        // Esc in the filter box clears it.
        if (e.Key == Key.Escape && ReferenceEquals(e.OriginalSource, SearchBox) && SearchBox.Text.Length > 0)
        {
            ViewModel.SearchText = "";
            e.Handled = true;
        }
    }

    void Window_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        // Clicking toolbar/action buttons right after typing in a delayed TextBox must commit first.
        if (Keyboard.FocusedElement is TextBox && e.OriginalSource is DependencyObject src &&
            FindAncestor<Button>(src) is not null)
        {
            CommitPendingEdit();
        }
    }

    void GroupGrid_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        // Delete removes the group, except while a cell (title) is being edited.
        if (e.Key != Key.Delete || Keyboard.Modifiers != ModifierKeys.None) return;
        if (sender is DataGrid grid && FindAncestor<TextBox>((DependencyObject)e.OriginalSource) is null &&
            ViewModel.DeleteGroupCommand.CanExecute(null))
        {
            ViewModel.DeleteGroupCommand.Execute(null);
            e.Handled = true;
            grid.Focus();
        }
    }

    void Find_Executed(object sender, ExecutedRoutedEventArgs e)
    {
        SearchBox.Focus();
        SearchBox.SelectAll();
    }

    void Issues_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is ListBox lb && lb.SelectedItem is Core.ValidationIssue issue)
        {
            ViewModel.ShowIssue(issue);
            lb.SelectedItem = null;  // allow clicking the same issue again
        }
    }

    void InsertPlaceholder_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string token }) return;
        var box = ArgumentsBox;
        int caret = box.CaretIndex;
        string text = box.Text ?? "";
        // Separate from neighbouring text with a space.
        string insert = token;
        if (caret > 0 && !char.IsWhiteSpace(text[caret - 1])) insert = " " + insert;
        if (caret < text.Length && !char.IsWhiteSpace(text[caret])) insert += " ";
        box.Text = text.Insert(caret, insert);
        box.CaretIndex = caret + insert.Length;
        box.Focus();
    }

    // ---- files dropped onto the window ------------------------------------------

    void Window_DragOver(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            e.Effects = DragDropEffects.Copy;
            e.Handled = true;
        }
    }

    void Window_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] files)
        {
            Activate();
            ViewModel.HandleDroppedFiles(files);
        }
    }

    // ---- drag & drop reordering -------------------------------------------------

    static T? FindAncestor<T>(DependencyObject? d) where T : DependencyObject
    {
        while (d is not null and not T) d = VisualTreeHelper.GetParent(d);
        return d as T;
    }

    void List_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        _dragStart = e.GetPosition(null);
        _dragItem = FindAncestor<ListViewItem>(e.OriginalSource as DependencyObject)?.DataContext as CommandViewModel;
    }

    void List_PreviewMouseUp(object sender, MouseButtonEventArgs e) => _dragItem = null;

    void List_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _dragItem is null) return;
        if (e.OriginalSource is DependencyObject src && FindAncestor<CheckBox>(src) is not null) return;
        var diff = _dragStart - e.GetPosition(null);
        if (Math.Abs(diff.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(diff.Y) < SystemParameters.MinimumVerticalDragDistance)
            return;
        DragDrop.DoDragDrop(CommandList, new DataObject(typeof(CommandViewModel), _dragItem), DragDropEffects.Move);
        _dragItem = null;
    }

    void List_DragOver(object sender, DragEventArgs e)
    {
        // Only handle reordering; file drops bubble up to the window.
        if (!e.Data.GetDataPresent(typeof(CommandViewModel))) return;
        e.Effects = DragDropEffects.Move;
        e.Handled = true;
    }

    void List_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(typeof(CommandViewModel)) is not CommandViewModel item) return;
        e.Handled = true;
        var target = FindAncestor<ListViewItem>(e.OriginalSource as DependencyObject)?.DataContext as CommandViewModel;
        int index = target is null ? ViewModel.Commands.Count - 1 : ViewModel.Commands.IndexOf(target);
        ViewModel.MoveTo(item, index);
    }
}
