using System.ComponentModel;
using System.Windows;
using ContextBridge.App.Resources;
using ContextBridge.Core;

namespace ContextBridge.App.Views;

sealed class ExportRow : INotifyPropertyChanged
{
    bool _selected;
    public required string Id { get; init; }
    public required string Label { get; init; }
    public bool Selected
    {
        get => _selected;
        set { _selected = value; PropertyChanged?.Invoke(this, new(nameof(Selected))); }
    }
    public event PropertyChangedEventHandler? PropertyChanged;
}

partial class ExportWindow : Window
{
    readonly List<ExportRow> _rows;

    public IReadOnlyList<string> SelectedIds => _rows.Where(r => r.Selected).Select(r => r.Id).ToList();
    public bool Generalize => GeneralizeBox.IsChecked == true;
    public string Description => DescriptionBox.Text;

    public ExportWindow(ConfigFile cfg, string? preselect)
    {
        InitializeComponent();
        _rows = cfg.Commands.Select(c =>
        {
            string group = cfg.Groups.FirstOrDefault(g => g.Id == c.Group)?.Title ?? "";
            string title = string.IsNullOrEmpty(c.Title) ? c.Id : c.Title;
            return new ExportRow
            {
                Id = c.Id,
                Label = group.Length > 0 ? group + Strings.Export_GroupSep + title : title,
                Selected = preselect is null || c.Id == preselect,
            };
        }).ToList();
        List.ItemsSource = _rows;
    }

    void All_Click(object sender, RoutedEventArgs e) { foreach (var r in _rows) r.Selected = true; }
    void None_Click(object sender, RoutedEventArgs e) { foreach (var r in _rows) r.Selected = false; }

    void Ok_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedIds.Count == 0)
        {
            AppDialog.Show(Strings.Msg_ExportChooseOne, "ContextBridge");
            return;
        }
        DialogResult = true;
    }
}
