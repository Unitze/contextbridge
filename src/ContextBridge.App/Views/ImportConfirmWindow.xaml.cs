using System.Windows;
using ContextBridge.App.Resources;
using ContextBridge.Core.Sharing;

namespace ContextBridge.App.Views;

/// <summary>Shows every command's program and arguments; nothing is imported without explicit approval.</summary>
partial class ImportConfirmWindow : Window
{
    readonly ImportPlan _plan;

    public ImportConfirmWindow(ImportPlan plan, string fileName)
    {
        InitializeComponent();
        _plan = plan;
        Header.Text = string.Format(Strings.Import_Header, fileName, plan.Items.Count);
        DescriptionText.Text = plan.Description ?? "";
        DescriptionText.Visibility = string.IsNullOrWhiteSpace(plan.Description) ? Visibility.Collapsed : Visibility.Visible;
        // Commands whose program cannot be found start unchecked.
        foreach (var item in plan.Items) item.Include = item.ProgramFound;
        Items.ItemsSource = plan.Items;
        Update();
    }

    void Update()
    {
        BlockingText.Visibility = _plan.HasUnresolvedPrograms ? Visibility.Visible : Visibility.Collapsed;
        OkButton.IsEnabled = Confirmed.IsChecked == true && _plan.Items.Any(i => i.Include);
    }

    void Include_Changed(object sender, RoutedEventArgs e) => Update();

    void SelectAll_Click(object sender, RoutedEventArgs e)
    {
        foreach (var item in _plan.Items) item.Include = true;
        Update();
    }

    void SelectNone_Click(object sender, RoutedEventArgs e)
    {
        foreach (var item in _plan.Items) item.Include = false;
        Update();
    }
    void Confirmed_Changed(object sender, RoutedEventArgs e) => Update();

    void Ok_Click(object sender, RoutedEventArgs e)
    {
        if (_plan.HasUnresolvedPrograms &&
            AppDialog.Show(Strings.Msg_ImportMissingPrograms, "ContextBridge",
                            MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;
        DialogResult = true;
    }
}
