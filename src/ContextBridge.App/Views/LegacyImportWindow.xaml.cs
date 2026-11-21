using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using ContextBridge.App.Resources;
using System.Windows.Data;
using System.Windows.Media;
using ContextBridge.App.Services;
using ContextBridge.Core;
using ContextBridge.Core.Legacy;

namespace ContextBridge.App.Views;

sealed class CandidateRow : INotifyPropertyChanged
{
    public required LegacyCandidate Candidate { get; init; }
    public ImageSource? Icon { get; init; }
    bool _include;

    public bool Include
    {
        get => _include;
        set { _include = value; PropertyChanged?.Invoke(this, new(nameof(Include))); Changed?.Invoke(); }
    }

    public Action? Changed { get; set; }
    public bool CanInclude => Candidate.Status is CandidateStatus.New or CandidateStatus.Changed;
    public string Title => Candidate.Title;
    public string Command => Candidate.RawCommand;

    public string StatusText => Candidate.Status switch
    {
        CandidateStatus.New => Candidate.ExcludedReason is null ? Strings.Legacy_StatusNew : Strings.Legacy_StatusNewExcluded,
        CandidateStatus.Imported => Strings.Legacy_StatusImported,
        CandidateStatus.Changed => Strings.Legacy_StatusChanged,
        _ => Strings.Legacy_StatusUnsupported,
    };

    public string Targets
    {
        get
        {
            var t = Candidate.Proposed?.Target;
            if (t is null) return "";
            var parts = new List<string>();
            if (t.ItemTypes.Contains(ItemTypes.File)) parts.Add(t.Extensions is { Count: > 0 } e ? string.Join(" ", e.Take(4)) + (e.Count > 4 ? " …" : "") : Strings.Target_Files);
            if (t.ItemTypes.Contains(ItemTypes.Folder)) parts.Add(Strings.Target_Folders);
            if (t.ItemTypes.Contains(ItemTypes.FolderBackground)) parts.Add(Strings.Target_BackgroundShort);
            if (t.ItemTypes.Contains(ItemTypes.Drive)) parts.Add(Strings.Target_Drives);
            return string.Join(" / ", parts);
        }
    }

    public string Details
    {
        get
        {
            var lines = new List<string> { string.Format(Strings.Legacy_DetailKeys, string.Join(", ", Candidate.Keys)) };
            if (Candidate.UnsupportedReason is not null) lines.Add(string.Format(Strings.Legacy_DetailReason, Candidate.UnsupportedReason));
            if (Candidate.ExcludedReason is not null) lines.Add(string.Format(Strings.Legacy_DetailExcluded, Candidate.ExcludedReason));
            if (Candidate.Proposed is { } p)
                lines.Add(string.Format(Strings.Legacy_DetailConverted, p.Action.Program, p.Action.Arguments,
                    p.Action.MultiFileMode == MultiFileModes.Each ? Strings.Mode_Each : Strings.Mode_Single));
            return string.Join("\n", lines);
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}

sealed class RemovedRow : INotifyPropertyChanged
{
    bool _remove;
    public required CommandDef Command { get; init; }
    public string Title => string.IsNullOrEmpty(Command.Title) ? Command.Id : Command.Title;
    public bool Remove
    {
        get => _remove;
        set { if (_remove == value) return; _remove = value; PropertyChanged?.Invoke(this, new(nameof(Remove))); Changed?.Invoke(); }
    }
    public Action? Changed { get; set; }
    public event PropertyChangedEventHandler? PropertyChanged;
}

partial class LegacyImportWindow : Window
{
    readonly LegacyScanner _scanner;
    readonly ConfigFile _config;
    readonly ProgramLocator _locator;
    List<CandidateRow> _rows = new();
    List<RemovedRow> _removed = new();
    bool _closed;

    public LegacyImportWindow(LegacyScanner scanner, ConfigFile config, ProgramLocator locator)
    {
        InitializeComponent();
        _scanner = scanner;
        _config = config;
        _locator = locator;
        Closed += (_, _) => _closed = true;
        Loaded += async (_, _) => await ScanAsync();
    }

    async Task ScanAsync()
    {
        ScanResult result;
        List<CandidateRow> builtRows;
        try
        {
            // Enumerating HKCR and extracting icons (Frozen BitmapSource) off the UI thread keeps the window responsive.
            (result, builtRows) = await Task.Run(() =>
            {
                var r = _scanner.Scan(_config);
                var rows = r.Candidates.Select(c => new CandidateRow
                {
                    Candidate = c,
                    Icon = c.Proposed is null ? null : IconLoader.For(c.Proposed, _locator),
                }).ToList();
                return (r, rows);
            });
        }
        catch (Exception ex)
        {
            if (!_closed) Loading.Text = string.Format(Strings.Legacy_ScanFailed, ex.Message);
            return;
        }
        if (_closed) return;

        Loading.Visibility = Visibility.Collapsed;
        _rows = builtRows;
        foreach (var r in _rows)
        {
            r.Include = r.CanInclude && r.Candidate.ExcludedReason is null;
            r.Changed = UpdateSummary;
        }
        // Leave removed items unchecked by default so users do not accidentally delete commands.
        _removed = result.RemovedFromRegistry.Select(c => new RemovedRow { Command = c, Remove = false, Changed = UpdateSummary }).ToList();
        RemovedList.ItemsSource = _removed;
        RemovedPanel.Visibility = _removed.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        RemovedPanel.IsExpanded = _removed.Count > 0;
        Grid.ItemsSource = new ListCollectionView(_rows) { Filter = FilterRow };
        OkButton.IsEnabled = true;
        UpdateSummary();
    }

    bool FilterRow(object o)
    {
        var r = (CandidateRow)o;
        if (r.Candidate.Status == CandidateStatus.Unsupported && ShowUnsupported.IsChecked != true) return false;
        if (r.Candidate.Status == CandidateStatus.Imported && ShowImported.IsChecked != true) return false;
        string q = SearchBox.Text.Trim();
        return q.Length == 0 || r.Title.Contains(q, StringComparison.OrdinalIgnoreCase) ||
               r.Command.Contains(q, StringComparison.OrdinalIgnoreCase);
    }

    void Filter_Changed(object sender, RoutedEventArgs e) => (Grid.ItemsSource as ListCollectionView)?.Refresh();

    /// <summary>Rows currently shown (search and filters applied).</summary>
    IEnumerable<CandidateRow> VisibleRows() =>
        (Grid.ItemsSource as ListCollectionView)?.Cast<CandidateRow>() ?? Enumerable.Empty<CandidateRow>();

    // Acts only on visible rows, so "search → select all" picks exactly what was found.
    // Default-excluded rows are included too: the user asked for everything explicitly.
    void SelectAll_Click(object sender, RoutedEventArgs e)
    {
        foreach (var r in VisibleRows().Where(r => r.CanInclude)) r.Include = true;
    }

    void SelectNone_Click(object sender, RoutedEventArgs e)
    {
        foreach (var r in VisibleRows()) r.Include = false;
    }

    void UpdateSummary()
    {
        int n = _rows.Count(r => r.Include);
        int news = _rows.Count(r => r.Candidate.Status == CandidateStatus.New);
        int changed = _rows.Count(r => r.Candidate.Status == CandidateStatus.Changed);
        int del = _removed.Count(r => r.Remove);
        Summary.Text = del > 0
            ? string.Format(Strings.Legacy_SummaryWithDelete, news, changed, n, del)
            : string.Format(Strings.Legacy_Summary, news, changed, n);
    }

    void Ok_Click(object sender, RoutedEventArgs e) => DialogResult = true;

    /// <summary>Applies the selection. Returns (added, updated, removed).</summary>
    public (int, int, int) Apply(ConfigFile cfg)
    {
        int added = 0, updated = 0, removed = 0;
        foreach (var r in _rows.Where(r => r.Include && r.Candidate.Proposed is not null))
        {
            var proposed = r.Candidate.Proposed!;
            if (r.Candidate.Status == CandidateStatus.Changed && r.Candidate.ExistingId is { } id)
            {
                var existing = cfg.Commands.FirstOrDefault(c => c.Id == id);
                if (existing is not null)
                {
                    // Keep the user's customizations of display (title, group, enabled, icon); refresh what was migrated.
                    existing.Action = proposed.Action;
                    existing.Target = proposed.Target;
                    existing.Origin = proposed.Origin;
                    updated++;
                    continue;
                }
            }
            var used = cfg.Commands.Select(c => c.Id).ToHashSet(StringComparer.Ordinal);
            proposed.Id = LegacyScanner.UniqueId(proposed.Id, used);
            cfg.Commands.Add(proposed);
            added++;
        }
        foreach (var r in _removed.Where(r => r.Remove))
        {
            if (cfg.Commands.Remove(r.Command)) removed++;
        }
        return (added, updated, removed);
    }
}
