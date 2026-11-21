using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Globalization;
using ContextBridge.App.Resources;
using ContextBridge.App.Services;
using ContextBridge.App.Views;
using ContextBridge.Core;
using ContextBridge.Core.Legacy;
using ContextBridge.Core.Sharing;
using Microsoft.Win32;

namespace ContextBridge.App.ViewModels;

sealed partial class GroupViewModel : ObservableObject
{
    readonly MainViewModel _owner;
    public GroupDef Model { get; }
    public GroupViewModel(GroupDef model, MainViewModel owner) { Model = model; _owner = owner; }

    public string Id => Model.Id;

    public string Title
    {
        get => Model.Title;
        set
        {
            if (Model.Title == value) return;
            Model.Title = value;
            OnPropertyChanged();
            _owner.OnGroupEdited();
        }
    }

    public ImageSource? Icon => IconLoader.For(Model.Icon, "", _owner.Locator);
    public string IconSummary => Model.Icon is null || Model.Icon.Source == IconSources.None
        ? Strings.Grp_IconNone
        : string.IsNullOrWhiteSpace(Model.Icon.Path) ? Strings.Grp_IconUnset : $"{Path.GetFileName(Model.Icon.Path)} ({Model.Icon.Index})";

    public int CommandCount => _owner.Commands.Count(c => c.Model.Group == Model.Id);
    public void Refresh()
    {
        OnPropertyChanged(nameof(CommandCount));
        OnPropertyChanged(nameof(Icon));
        OnPropertyChanged(nameof(IconSummary));
    }
}

sealed record GroupChoice(string Id, string Title);

sealed partial class MainViewModel : ObservableObject
{
    readonly ConfigStore _store = new();
    readonly ConcurrentDictionary<string, string?> _programCache = new(StringComparer.OrdinalIgnoreCase);
    readonly ConcurrentDictionary<string, byte> _programInFlight = new(StringComparer.OrdinalIgnoreCase);
    ConfigFile _config = new();
    DateTime _loadedWriteTime;
    bool _suspendDirty;
    internal bool IsRebuildingGroups { get; private set; }

    public ProgramLocator Locator { get; } = new(appPaths: WindowsRegistryView.AppPath);

    public ObservableCollection<CommandViewModel> Commands { get; } = new();
    public ObservableCollection<GroupViewModel> Groups { get; } = new();
    public ObservableCollection<GroupChoice> GroupChoices { get; } = new();
    public ObservableCollection<ValidationIssue> Issues { get; } = new();

    public IReadOnlyList<KeyValuePair<string, string>> WindowChoices { get; } = new[]
    {
        new KeyValuePair<string, string>(WindowModes.Normal, Strings.Window_Normal),
        new KeyValuePair<string, string>(WindowModes.Minimized, Strings.Window_Minimized),
        new KeyValuePair<string, string>(WindowModes.Maximized, Strings.Window_Maximized),
        new KeyValuePair<string, string>(WindowModes.Hidden, Strings.Window_Hidden),
    };

    public IReadOnlyList<KeyValuePair<string, string>> IconChoices { get; } = new[]
    {
        new KeyValuePair<string, string>(IconSources.Program, Strings.Icon_FromProgram),
        new KeyValuePair<string, string>(IconSources.File, Strings.Icon_File),
        new KeyValuePair<string, string>(IconSources.None, Strings.Icon_None),
    };

    public IReadOnlyList<KeyValuePair<string, string>> LanguageChoices { get; } =
        new[] { new KeyValuePair<string, string>("", Strings.Lang_System) }
            .Concat(UiLanguage.Supported.Select(t => new KeyValuePair<string, string>(t, UiLanguage.NativeName(t))))
            .ToList();

    /// <summary>App display language ("" = Windows). Takes effect after a restart.</summary>
    public string Language
    {
        get => AppPreferences.Current.Language;
        set
        {
            value ??= "";
            if (AppPreferences.Current.Language == value) return;
            AppPreferences.Current.Language = value;
            AppPreferences.Current.Save();
            OnPropertyChanged();
            Application.Current?.Dispatcher.BeginInvoke(PromptRestartForLanguage);
        }
    }

    void PromptRestartForLanguage()
    {
        if (MessageBox.Show(Strings.Msg_LanguageRestart, "ContextBridge", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;
        if (!ConfirmClose()) return;
        _closeConfirmed = true;
        try
        {
            if (Environment.ProcessPath is { } exe)
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(exe) { UseShellExecute = false })?.Dispose();
        }
        catch (Exception)
        {
            return;
        }
        Application.Current.MainWindow?.Close();
    }

    bool _closeConfirmed;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(DeleteCommand), nameof(DuplicateCommand), nameof(MoveUpCommand), nameof(MoveDownCommand), nameof(DeleteGroupOfSelectedCommand))]
    private CommandViewModel? _selected;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(DeleteGroupCommand), nameof(BrowseGroupIconCommand), nameof(ClearGroupIconCommand))]
    private GroupViewModel? _selectedGroup;
    [ObservableProperty] [NotifyCanExecuteChangedFor(nameof(SaveCommand))] [NotifyPropertyChangedFor(nameof(WindowTitle))]
    private bool _isDirty;

    /// <summary>"ContextBridge *" while there are unsaved changes.</summary>
    public string WindowTitle => IsDirty ? "ContextBridge *" : "ContextBridge";

    [ObservableProperty] private string _searchText = "";

    /// <summary>Filtered view of <see cref="Commands"/> for the list (reordering always uses the full list).</summary>
    public ICollectionView CommandsView { get; }

    public MainViewModel()
    {
        CommandsView = CollectionViewSource.GetDefaultView(Commands);
        CommandsView.Filter = o =>
        {
            if (string.IsNullOrWhiteSpace(SearchText)) return true;
            var c = (CommandViewModel)o;
            string q = SearchText.Trim();
            return c.DisplayTitle.Contains(q, StringComparison.CurrentCultureIgnoreCase) ||
                   c.Program.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                   c.GroupTitle.Contains(q, StringComparison.CurrentCultureIgnoreCase);
        };
    }

    /// <summary>A filter is active and hides every command.</summary>
    public bool HasNoSearchResults => Commands.Count > 0 && !string.IsNullOrWhiteSpace(SearchText) && CommandsView.IsEmpty;

    partial void OnSearchTextChanged(string value)
    {
        CommandsView.Refresh();
        OnPropertyChanged(nameof(HasNoSearchResults));
        MoveUpCommand.NotifyCanExecuteChanged();
        MoveDownCommand.NotifyCanExecuteChanged();
    }
    [ObservableProperty] private string _packageMessage = "";
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RegisterPackageCommand))]
    [NotifyPropertyChangedFor(nameof(HasPackageAction))]
    private string _packageAction = "";
    public bool HasPackageAction => !string.IsNullOrEmpty(PackageAction);
    [ObservableProperty] private bool _packageNeedsAttention;
    [ObservableProperty] private string _statusText = "";

    public (bool Resolved, string? Path) TryGetLocatedProgram(string program)
    {
        if (string.IsNullOrWhiteSpace(program)) return (true, null);
        if (_programCache.TryGetValue(program, out var cached)) return (true, cached);
        ScheduleProgramResolve(program);
        return (false, null);
    }

    public void InvalidateProgramCache(string? program = null)
    {
        if (program is null)
        {
            _programCache.Clear();
            return;
        }
        _programCache.TryRemove(program, out _);
        ScheduleProgramResolve(program);
    }

    void ScheduleProgramResolve(string program)
    {
        if (string.IsNullOrWhiteSpace(program) || !_programInFlight.TryAdd(program, 0)) return;
        _ = Task.Run(() =>
        {
            string? resolved = null;
            try { resolved = Locator.Locate(program); }
            catch { resolved = null; }
            _programCache[program] = resolved;
            _programInFlight.TryRemove(program, out _);
            Application.Current?.Dispatcher.BeginInvoke(() =>
            {
                foreach (var c in Commands)
                {
                    if (string.Equals(c.Program, program, StringComparison.OrdinalIgnoreCase))
                        c.RefreshProgramStatus();
                }
                Revalidate();
            });
        });
    }

    bool CachedProgramExists(string program)
    {
        var (resolved, path) = TryGetLocatedProgram(program);
        // While background resolution is still in flight, do not flash a false "missing" warning.
        return !resolved || path is not null;
    }

    public string ConfigPath => _store.ConfigPath;

    public string MenuTitle
    {
        get => _config.Settings?.MenuTitle ?? "";
        set
        {
            _config.Settings ??= new SettingsDef();
            _config.Settings.MenuTitle = string.IsNullOrWhiteSpace(value) ? null : value;
            OnPropertyChanged();
            MarkDirty();
        }
    }

    public bool Logging
    {
        get => _config.Settings?.Logging ?? false;
        set
        {
            _config.Settings ??= new SettingsDef();
            _config.Settings.Logging = value;
            OnPropertyChanged();
            MarkDirty();
        }
    }

    public string Version => PackageService.AppVersion;

    // ---------------------------------------------------------------- loading / saving

    /// <summary>Returns false if the user chose to exit instead of recovering from a broken file.</summary>
    public bool Load()
    {
        bool restoredFromBackup = false;
        try
        {
            _config = _store.Load();
        }
        catch (Exception ex)
        {
            var r = InputDialog.Ask(null, "ContextBridge",
                string.Format(Strings.Msg_LoadFailed, ex.Message, _store.ConfigPath),
                Strings.Msg_LoadFailedHint,
                new[] { Strings.Btn_RestoreBackup, Strings.Btn_StartEmpty, Strings.Btn_Exit });
            if (r is < 0 or 2) return false;
            _config = new ConfigFile();
            if (r == 0)
            {
                if (!File.Exists(_store.BackupPath))
                {
                    MessageBox.Show(Strings.Msg_NoBackup, "ContextBridge");
                }
                else
                {
                    try
                    {
                        _config = ConfigSerializer.Parse(File.ReadAllText(_store.BackupPath));
                        restoredFromBackup = true;
                    }
                    catch
                    {
                        MessageBox.Show(Strings.Msg_BackupBroken, "ContextBridge");
                    }
                }
            }
        }
        _loadedWriteTime = File.Exists(_store.ConfigPath) ? File.GetLastWriteTimeUtc(_store.ConfigPath) : default;
        Rebuild();
        IsDirty = restoredFromBackup;
        StatusText = restoredFromBackup
            ? Strings.Status_RestoredFromBackup
            : (_store.Exists ? string.Format(Strings.Status_Loaded, _store.ConfigPath) : Strings.Status_New);
        return true;
    }

    void Rebuild(CommandDef? select = null)
    {
        _suspendDirty = true;
        Commands.Clear();
        foreach (var c in _config.Commands) Commands.Add(new CommandViewModel(c, this));
        Groups.Clear();
        foreach (var g in _config.Groups) Groups.Add(new GroupViewModel(g, this));
        RebuildGroupChoices();
        Selected = select is null ? Commands.FirstOrDefault() : Commands.FirstOrDefault(c => c.Model == select);
        OnPropertyChanged(nameof(MenuTitle));
        OnPropertyChanged(nameof(Logging));
        _suspendDirty = false;
        Revalidate();
        OnPropertyChanged(nameof(HasNoSearchResults));
    }

    void RebuildGroupChoices()
    {
        IsRebuildingGroups = true;
        try
        {
            GroupChoices.Clear();
            GroupChoices.Add(new GroupChoice("", Strings.Grp_NoneChoice));
            foreach (var g in _config.Groups) GroupChoices.Add(new GroupChoice(g.Id, g.Title));
        }
        finally
        {
            IsRebuildingGroups = false;
        }
    }

    bool CanSave() => IsDirty;

    [RelayCommand(CanExecute = nameof(CanSave))]
    async Task Save()
    {
        if (TrySave() && RestartExplorerOnSave) await RestartExplorerCore(afterSave: true);
    }

    /// <summary>Save (when there is something to save) and restart Explorer regardless.</summary>
    [RelayCommand]
    async Task SaveAndRestartExplorer()
    {
        bool saved = false;
        if (IsDirty)
        {
            if (!TrySave()) return;
            saved = true;
        }
        await RestartExplorerCore(afterSave: saved);
    }

    // ---------------------------------------------------------------- Explorer restart

    /// <summary>App-only preference (not part of commands.json, so it never marks the config dirty).</summary>
    public bool RestartExplorerOnSave
    {
        get => AppPreferences.Current.RestartExplorerOnSave;
        set
        {
            if (AppPreferences.Current.RestartExplorerOnSave == value) return;
            AppPreferences.Current.RestartExplorerOnSave = value;
            AppPreferences.Current.Save();
            OnPropertyChanged();
            OnPropertyChanged(nameof(SaveToolTip));
        }
    }

    public string SaveToolTip => RestartExplorerOnSave ? Strings.Main_SaveTipRestart : Strings.Main_SaveTip;

    [ObservableProperty] [NotifyCanExecuteChangedFor(nameof(RestartExplorerCommand))]
    private bool _isRestartingExplorer;

    bool CanRestartExplorer() => !IsRestartingExplorer;

    [RelayCommand(CanExecute = nameof(CanRestartExplorer))]
    async Task RestartExplorer()
    {
        if (MessageBox.Show(Strings.Msg_RestartExplorerConfirm,
                            "ContextBridge", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK)
            return;
        await RestartExplorerCore(afterSave: false);
    }

    async Task RestartExplorerCore(bool afterSave)
    {
        if (IsRestartingExplorer) return;
        IsRestartingExplorer = true;
        string saved = afterSave ? string.Format(CultureInfo.CurrentCulture, Strings.Status_SavedShort, DateTime.Now) : "";
        StatusText = saved + Strings.Status_RestartingExplorer;
        try
        {
            var error = await Task.Run(ExplorerRestarter.Restart);
            if (error is null)
            {
                StatusText = saved + Strings.Status_ExplorerRestarted;
            }
            else
            {
                StatusText = saved + Strings.Status_ExplorerRestartFailed;
                MessageBox.Show(string.Format(Strings.Msg_ExplorerRestartFailed, error), "ContextBridge", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
        finally
        {
            IsRestartingExplorer = false;
        }
    }

    /// <summary>Synchronous variant for the "save on close" path (the window is going away).</summary>
    void RestartExplorerBlocking()
    {
        var error = ExplorerRestarter.Restart();
        if (error is not null)
            MessageBox.Show(string.Format(Strings.Msg_ExplorerRestartFailed, error), "ContextBridge", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    public bool TrySave()
    {
        var issues = ConfigValidator.Validate(_config, CachedProgramExists);
        if (ConfigValidator.HasErrors(issues))
        {
            MessageBox.Show(string.Format(Strings.Msg_SaveBlocked,
                                string.Join("\n", issues.Where(i => i.Severity == Severity.Error).Select(i => Strings.Common_Bullet + i.Message))),
                            "ContextBridge", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }
        if (File.Exists(_store.ConfigPath) && File.GetLastWriteTimeUtc(_store.ConfigPath) != _loadedWriteTime)
        {
            var r = MessageBox.Show(Strings.Msg_ExternalChange, "ContextBridge",
                                    MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (r != MessageBoxResult.Yes) return false;
        }
        try
        {
            _store.Save(_config);
        }
        catch (Exception ex)
        {
            MessageBox.Show(string.Format(Strings.Msg_SaveFailed, ex.Message), "ContextBridge", MessageBoxButton.OK, MessageBoxImage.Error);
            return false;
        }
        CleanupUnusedIcons();
        _loadedWriteTime = File.GetLastWriteTimeUtc(_store.ConfigPath);
        IsDirty = false;
        StatusText = string.Format(CultureInfo.CurrentCulture, Strings.Status_Saved, DateTime.Now);
        return true;
    }

    void CleanupUnusedIcons()
    {
        try
        {
            if (!Directory.Exists(_store.IconDirectory)) return;
            var referenced = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var c in _config.Commands)
                if (!string.IsNullOrWhiteSpace(c.Icon.Path)) referenced.Add(Path.GetFullPath(Template.ExpandProgram(c.Icon.Path!)));
            foreach (var g in _config.Groups)
                if (!string.IsNullOrWhiteSpace(g.Icon?.Path)) referenced.Add(Path.GetFullPath(Template.ExpandProgram(g.Icon!.Path!)));
            foreach (var file in Directory.GetFiles(_store.IconDirectory, "*.ico"))
            {
                if (!referenced.Contains(Path.GetFullPath(file)))
                {
                    try { File.Delete(file); } catch { }
                }
            }
        }
        catch
        {
        }
    }

    [RelayCommand]
    void Reload()
    {
        if (IsDirty && MessageBox.Show(Strings.Msg_DiscardAndReload, "ContextBridge",
                                       MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;
        IconLoader.Invalidate();
        InvalidateProgramCache();
        Load();
    }

    /// <summary>Returns false if the user cancelled closing.</summary>
    public bool ConfirmClose()
    {
        if (!IsDirty || _closeConfirmed) return true;
        var r = MessageBox.Show(Strings.Msg_SaveChanges, "ContextBridge", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
        return r switch
        {
            MessageBoxResult.Yes => SaveForClose(),
            MessageBoxResult.No => true,
            _ => false,
        };
    }

    bool SaveForClose()
    {
        if (!TrySave()) return false;
        if (RestartExplorerOnSave) RestartExplorerBlocking();
        return true;
    }

    void MarkDirty()
    {
        if (_suspendDirty) return;
        IsDirty = true;
        Revalidate();
    }

    public void OnCommandEdited(CommandViewModel vm)
    {
        MarkDirty();
        DeleteGroupOfSelectedCommand.NotifyCanExecuteChanged();
        foreach (var g in Groups) g.Refresh();
    }

    public void OnGroupEdited()
    {
        RebuildGroupChoices();
        foreach (var c in Commands) c.RefreshAll();
        MarkDirty();
    }

    public string GroupTitleOf(string? id) =>
        string.IsNullOrEmpty(id) ? "" : _config.Groups.FirstOrDefault(g => g.Id == id)?.Title ?? $"({id})";

    void Revalidate()
    {
        Issues.Clear();
        var all = ConfigValidator.Validate(_config, CachedProgramExists);
        foreach (var i in all) Issues.Add(i);
        foreach (var c in Commands)
            c.SetIssues(all.Where(i => i.CommandId == c.Id).ToList());
    }

    static string ProgramFilter(string patterns) =>
        $"{Strings.Filter_Programs} ({patterns})|{patterns}|{Strings.Filter_AllFiles} (*.*)|*.*";

    /// <summary>Jumps to the command an issue refers to.</summary>
    public void ShowIssue(ValidationIssue? issue)
    {
        if (issue?.CommandId is not { } id) return;
        var target = Commands.FirstOrDefault(c => c.Id == id);
        if (target is null) return;
        if (!CommandsView.Contains(target)) SearchText = "";
        Selected = target;
    }

    // ---------------------------------------------------------------- command list editing

    string NewId(string baseId)
    {
        var used = _config.Commands.Select(c => c.Id).ToHashSet(StringComparer.Ordinal);
        return LegacyScanner.UniqueId(baseId, used);
    }

    [RelayCommand]
    void Add()
    {
        var dlg = new OpenFileDialog
        {
            Title = Strings.Dlg_AddProgramTitle,
            Filter = ProgramFilter("*.exe;*.bat;*.cmd"),
        };
        if (dlg.ShowDialog() != true) return;
        AddProgram(dlg.FileName);
    }

    /// <summary>Adds a command for a program (toolbar, or an .exe/.lnk dropped onto the window).</summary>
    public void AddProgram(string program)
    {
        string stem = Path.GetFileNameWithoutExtension(program);
        var cmd = new CommandDef
        {
            Id = NewId(string.IsNullOrEmpty(stem) ? "command" : LegacyScanner.Slug(stem, "command")),
            Title = string.IsNullOrEmpty(stem) ? Strings.Cmd_NewCommand : Template.AutoTitle(stem, CultureInfo.InstalledUICulture),
            Action = new ActionDef { Program = program, Arguments = "%*" },
            Target = new TargetDef { ItemTypes = new() { ItemTypes.File }, MinSelection = 1 },
        };
        int at = Selected is null ? _config.Commands.Count : _config.Commands.IndexOf(Selected.Model) + 1;
        _config.Commands.Insert(at, cmd);
        Rebuild(cmd);
        MarkDirty();
    }

    bool HasSelection() => Selected is not null;

    [RelayCommand(CanExecute = nameof(HasSelection))]
    void Duplicate()
    {
        if (Selected is null) return;
        var copy = Selected.Model.Clone();
        copy.Id = NewId(Selected.Model.Id);
        copy.Title = Selected.DisplayTitle + Strings.Cmd_CopySuffix;
        copy.Origin = null;
        _config.Commands.Insert(_config.Commands.IndexOf(Selected.Model) + 1, copy);
        Rebuild(copy);
        MarkDirty();
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    void Delete()
    {
        if (Selected is null) return;
        if (MessageBox.Show(string.Format(Strings.Msg_DeleteCommand, Selected.DisplayTitle), "ContextBridge", MessageBoxButton.YesNo,
                            MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;
        int idx = _config.Commands.IndexOf(Selected.Model);
        _config.Commands.RemoveAt(idx);
        Rebuild(_config.Commands.Count == 0 ? null : _config.Commands[Math.Min(idx, _config.Commands.Count - 1)]);
        MarkDirty();
    }

    List<CommandViewModel> VisibleCommands() => CommandsView.Cast<CommandViewModel>().ToList();

    bool CanMoveUp()
    {
        if (Selected is null) return false;
        var visible = VisibleCommands();
        int vi = visible.IndexOf(Selected);
        return vi > 0;
    }

    bool CanMoveDown()
    {
        if (Selected is null) return false;
        var visible = VisibleCommands();
        int vi = visible.IndexOf(Selected);
        return vi >= 0 && vi < visible.Count - 1;
    }

    [RelayCommand(CanExecute = nameof(CanMoveUp))]
    void MoveUp() => Move(-1);

    [RelayCommand(CanExecute = nameof(CanMoveDown))]
    void MoveDown() => Move(+1);

    void Move(int delta)
    {
        if (Selected is null) return;
        var visible = VisibleCommands();
        int vi = visible.IndexOf(Selected);
        int vj = vi + delta;
        if (vi < 0 || vj < 0 || vj >= visible.Count) return;
        int i = _config.Commands.IndexOf(Selected.Model);
        int j = _config.Commands.IndexOf(visible[vj].Model);
        if (i < 0 || j < 0 || i == j) return;
        var m = Selected.Model;
        _config.Commands.RemoveAt(i);
        _config.Commands.Insert(j, m);
        Commands.Move(i, j);
        MoveUpCommand.NotifyCanExecuteChanged();
        MoveDownCommand.NotifyCanExecuteChanged();
        MarkDirty();
    }

    /// <summary>Drag-and-drop reorder from the list view.</summary>
    public void MoveTo(CommandViewModel item, int newIndex)
    {
        if (_config.Commands.Count == 0) return;
        int i = _config.Commands.IndexOf(item.Model);
        newIndex = Math.Clamp(newIndex, 0, _config.Commands.Count - 1);
        if (i < 0 || i == newIndex) return;
        _config.Commands.RemoveAt(i);
        _config.Commands.Insert(newIndex, item.Model);
        Commands.Move(i, newIndex);
        Selected = item;
        MoveUpCommand.NotifyCanExecuteChanged();
        MoveDownCommand.NotifyCanExecuteChanged();
        MarkDirty();
    }

    [RelayCommand]
    void BrowseProgram()
    {
        if (Selected is null) return;
        var dlg = new OpenFileDialog
        {
            Filter = ProgramFilter("*.exe;*.bat;*.cmd;*.com"),
        };
        if (dlg.ShowDialog() == true) Selected.Program = dlg.FileName;
    }

    static string SafeIconBaseName(string id)
    {
        var slug = LegacyScanner.Slug(id, "icon");
        if (slug.Length > 40) slug = slug[..40];
        // Unique suffix prevents overwriting an active icon before the user saves (cancel stays safe).
        return $"{slug}-{Guid.NewGuid().ToString("N")[..8]}";
    }

    string? PickIconFile(string ownerId)
    {
        var dlg = new OpenFileDialog
        {
            Filter = $"{Strings.Filter_Icons} (*.ico;*.exe;*.dll;*.png;*.jpg;*.bmp)|*.ico;*.exe;*.dll;*.png;*.jpg;*.jpeg;*.bmp|{Strings.Filter_AllFiles} (*.*)|*.*",
        };
        if (dlg.ShowDialog() != true) return null;
        string path = dlg.FileName;
        string ext = Path.GetExtension(path).ToLowerInvariant();
        if (ext is not (".ico" or ".exe" or ".dll"))
        {
            // The shell cannot use PNG/JPEG directly; convert to .ico in the data folder.
            try
            {
                path = IconLoader.ConvertToIco(path, _store.IconDirectory, SafeIconBaseName(ownerId));
            }
            catch (Exception ex)
            {
                MessageBox.Show(string.Format(Strings.Msg_ImageConvertFailed, ex.Message), "ContextBridge");
                return null;
            }
        }
        return path;
    }

    [RelayCommand]
    void BrowseIcon()
    {
        if (Selected is null) return;
        var path = PickIconFile(Selected.Id);
        if (path is null) return;
        Selected.IconSource = IconSources.File;
        Selected.IconPath = path;
        Selected.IconIndex = 0;
    }

    // ---------------------------------------------------------------- groups

    bool HasSelectedGroup() => SelectedGroup is not null;

    [RelayCommand]
    void AddGroup()
    {
        var used = _config.Groups.Select(g => g.Id).ToHashSet(StringComparer.Ordinal);
        var g = new GroupDef { Id = LegacyScanner.UniqueId("group", used), Title = Strings.Grp_NewGroup };
        _config.Groups.Add(g);
        Groups.Add(new GroupViewModel(g, this));
        SelectedGroup = Groups[^1];
        RebuildGroupChoices();
        MarkDirty();
    }

    [RelayCommand(CanExecute = nameof(HasSelectedGroup))]
    void DeleteGroup()
    {
        if (SelectedGroup is null) return;
        var g = SelectedGroup.Model;
        int index = Groups.IndexOf(SelectedGroup);
        var members = _config.Commands.Where(c => c.Group == g.Id).ToList();
        bool deleteMembers = false;
        if (members.Count > 0)
        {
            int r = InputDialog.Ask(Application.Current.MainWindow, Strings.Dlg_DeleteGroupTitle,
                string.Format(Strings.Msg_DeleteGroup, g.Title, members.Count), null,
                new[] { Strings.Btn_MoveToTop, Strings.Btn_DeleteWithCommands });
            if (r < 0) return;
            deleteMembers = r == 1;
        }
        foreach (var c in members)
        {
            if (deleteMembers) _config.Commands.Remove(c);
            else c.Group = null;
        }
        _config.Groups.Remove(g);
        var keep = Selected?.Model is { } sel && _config.Commands.Contains(sel) ? sel : null;
        Rebuild(keep);
        SelectedGroup = Groups.Count == 0 ? null : Groups[Math.Min(index, Groups.Count - 1)];
        MarkDirty();
        StatusText = members.Count == 0
            ? string.Format(Strings.Status_GroupDeleted, g.Title)
            : deleteMembers
                ? string.Format(Strings.Status_GroupAndCommandsDeleted, g.Title, members.Count)
                : string.Format(Strings.Status_GroupDeletedMoved, g.Title, members.Count);
    }

    /// <summary>Delete from the command editor: removes the group the selected command belongs to.</summary>
    bool HasSelectedCommandGroup() => Selected is not null && !string.IsNullOrEmpty(Selected.GroupId);

    [RelayCommand(CanExecute = nameof(HasSelectedCommandGroup))]
    void DeleteGroupOfSelected()
    {
        if (Selected is null) return;
        var vm = Groups.FirstOrDefault(x => x.Id == Selected.GroupId);
        if (vm is null) return;
        SelectedGroup = vm;
        DeleteGroup();
    }

    [RelayCommand(CanExecute = nameof(HasSelectedGroup))]
    void BrowseGroupIcon()
    {
        if (SelectedGroup is null) return;
        var path = PickIconFile("group-" + SelectedGroup.Id);
        if (path is null) return;
        SelectedGroup.Model.Icon = new IconDef { Source = IconSources.File, Path = path, Index = 0 };
        SelectedGroup.Refresh();
        MarkDirty();
    }

    [RelayCommand(CanExecute = nameof(HasSelectedGroup))]
    void ClearGroupIcon()
    {
        if (SelectedGroup is null || SelectedGroup.Model.Icon is null) return;
        SelectedGroup.Model.Icon = null;
        SelectedGroup.Refresh();
        MarkDirty();
    }

    [RelayCommand]
    void NewGroupForSelected()
    {
        if (Selected is null) return;
        var title = InputDialog.Show(Application.Current.MainWindow, Strings.Dlg_NewGroupTitle, Strings.Dlg_NewGroupLabel, "");
        if (string.IsNullOrWhiteSpace(title)) return;
        var used = _config.Groups.Select(g => g.Id).ToHashSet(StringComparer.Ordinal);
        var slug = LegacyScanner.Slug(title, "");
        var baseId = string.IsNullOrEmpty(slug) ? "group" : "group-" + slug;
        var g = new GroupDef { Id = LegacyScanner.UniqueId(baseId, used), Title = title.Trim() };
        _config.Groups.Add(g);
        Groups.Add(new GroupViewModel(g, this));
        RebuildGroupChoices();
        Selected.GroupId = g.Id;
        Selected.RefreshAll();
    }

    /// <summary>
    /// Runs the selected command against files/folders picked here, exactly as the handler would
    /// (same matching and expansion), so it can be checked without opening Explorer.
    /// </summary>
    [RelayCommand]
    async Task TryRun()
    {
        if (Selected is null) return;
        var c = Selected.Model;
        if (string.IsNullOrWhiteSpace(c.Action.Program))
        {
            MessageBox.Show(Strings.Msg_NoProgram, "ContextBridge", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        // Let the user choose which target kind to simulate when a command supports multiple kinds.
        var modes = new List<(string Kind, string Label)>();
        if (c.Target.Includes(ItemTypes.File)) modes.Add((ItemTypes.File, Strings.Try_File));
        if (c.Target.Includes(ItemTypes.Folder)) modes.Add((ItemTypes.Folder, Strings.Try_Folder));
        if (c.Target.Includes(ItemTypes.FolderBackground)) modes.Add((ItemTypes.FolderBackground, Strings.Try_Background));
        if (c.Target.Includes(ItemTypes.Drive)) modes.Add((ItemTypes.Drive, Strings.Try_Drive));
        if (modes.Count == 0) modes.Add((ItemTypes.File, Strings.Try_File));

        string chosenKind = modes[0].Kind;
        if (modes.Count > 1)
        {
            int idx = InputDialog.Choose(Application.Current.MainWindow, Strings.Dlg_TryRunTitle, Strings.Dlg_TryRunWhich,
                                         modes.Select(m => m.Label).ToList());
            if (idx < 0) return;
            chosenKind = modes[idx].Kind;
        }

        SelectionInfo sel;
        if (chosenKind == ItemTypes.File)
        {
            var dlg = new OpenFileDialog { Title = Strings.Dlg_TryFile, Multiselect = true };
            if (dlg.ShowDialog() != true) return;
            sel = new SelectionInfo(dlg.FileNames.Select(f => new SelectedEntry(f, SelectedKind.File)).ToList());
        }
        else if (chosenKind == ItemTypes.FolderBackground)
        {
            var dlg = new OpenFolderDialog { Title = Strings.Dlg_TryBackground, Multiselect = false };
            if (dlg.ShowDialog() != true) return;
            sel = SelectionInfo.Background(dlg.FolderName);
        }
        else
        {
            var dlg = new OpenFolderDialog
            {
                Title = chosenKind == ItemTypes.Drive ? Strings.Dlg_TryDrive : Strings.Dlg_TryFolder,
                Multiselect = chosenKind == ItemTypes.Folder,
            };
            if (dlg.ShowDialog() != true) return;
            sel = new SelectionInfo(dlg.FolderNames.Select(f => new SelectedEntry(f,
                LaunchPlanner.IsRootPath(f) ? SelectedKind.Drive : SelectedKind.Folder)).ToList());
        }

        var why = LaunchPlanner.WhyHidden(c, sel);
        if (why is not null &&
            MessageBox.Show(string.Format(Strings.Msg_WouldBeHidden, why), "ContextBridge",
                            MessageBoxButton.YesNo, MessageBoxImage.Information) != MessageBoxResult.Yes)
            return;

        var plans = LaunchPlanner.Plan(c, sel, Template.ProcessEnv);
        if (plans.Count > 5 &&
            MessageBox.Show(string.Format(Strings.Msg_ManyLaunches, plans.Count), "ContextBridge",
                            MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK)
            return;

        string title = Selected.DisplayTitle;
        var error = await Task.Run(() =>
        {
            foreach (var p in plans)
            {
                var psi = new System.Diagnostics.ProcessStartInfo(p.Program, p.Arguments)
                {
                    UseShellExecute = true,
                    WorkingDirectory = Directory.Exists(p.WorkingDirectory) ? p.WorkingDirectory : "",
                    Verb = p.RunAsAdmin ? "runas" : "",
                    WindowStyle = p.Window switch
                    {
                        WindowModes.Minimized => System.Diagnostics.ProcessWindowStyle.Minimized,
                        WindowModes.Maximized => System.Diagnostics.ProcessWindowStyle.Maximized,
                        WindowModes.Hidden => System.Diagnostics.ProcessWindowStyle.Hidden,
                        _ => System.Diagnostics.ProcessWindowStyle.Normal,
                    },
                };
                try
                {
                    System.Diagnostics.Process.Start(psi)?.Dispose();
                }
                catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
                {
                    return (string?)null;  // UAC declined
                }
                catch (Exception ex)
                {
                    return string.Format(Strings.Msg_LaunchFailed, $"{Template.QuoteArg(p.Program)} {p.Arguments}", ex.Message);
                }
            }
            return (string?)null;
        });

        if (error is not null)
        {
            MessageBox.Show(error, "ContextBridge", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }
        StatusText = string.Format(Strings.Status_TryRunDone, title, plans.Count);
    }

    /// <summary>Files dropped onto the window: programs (.exe/.lnk/...) become commands, .json files are imported.</summary>
    public void HandleDroppedFiles(string[] paths)
    {
        int handled = 0;
        foreach (var p in paths)
        {
            string ext = Path.GetExtension(p).ToLowerInvariant();
            if (ext == ".json")
            {
                ImportFile(p);
                handled++;
            }
            else if (ext is ".exe" or ".bat" or ".cmd" or ".com")
            {
                AddProgram(p);
                handled++;
            }
            else if (ext == ".lnk")
            {
                var target = NativeMethods.ResolveShortcut(p);
                if (target is not null && File.Exists(target))
                {
                    AddProgram(target);
                    handled++;
                }
            }
        }
        if (handled == 0 && paths.Length > 0)
            StatusText = Strings.Status_DropUnsupported;
    }

    // ---------------------------------------------------------------- legacy migration

    [RelayCommand]
    void ImportLegacy()
    {
        var scanner = new LegacyScanner(new WindowsRegistryView(), NativeMethods.LoadIndirectString);
        var win = new LegacyImportWindow(scanner, _config, Locator) { Owner = Application.Current.MainWindow };
        if (win.ShowDialog() != true) return;
        var (added, updated, removed) = win.Apply(_config);
        if (added == 0 && updated == 0 && removed == 0)
        {
            StatusText = Strings.Status_NoChanges;
            return;
        }
        Rebuild(Selected?.Model);
        MarkDirty();
        StatusText = string.Format(Strings.Status_LegacyApplied, added, updated, removed);
    }

    // ---------------------------------------------------------------- sharing

    [RelayCommand]
    void Export()
    {
        if (_config.Commands.Count == 0)
        {
            MessageBox.Show(Strings.Msg_NothingToExport, "ContextBridge");
            return;
        }
        var win = new ExportWindow(_config, Selected?.Model.Id) { Owner = Application.Current.MainWindow };
        if (win.ShowDialog() != true) return;
        var doc = ShareExporter.Export(_config, win.SelectedIds, win.Generalize);
        doc.Description = string.IsNullOrWhiteSpace(win.Description) ? null : win.Description;
        var dlg = new SaveFileDialog
        {
            Filter = $"{Strings.Filter_Share} (*{ShareDocument.FileSuffix})|*{ShareDocument.FileSuffix}|JSON (*.json)|*.json",
            FileName = (doc.Commands.Count == 1 ? LegacyScanner.Slug(doc.Commands[0].Id, "command") : "commands") + ShareDocument.FileSuffix,
        };
        if (dlg.ShowDialog() != true) return;
        try
        {
            File.WriteAllText(dlg.FileName, ShareExporter.Serialize(doc), new System.Text.UTF8Encoding(false));
        }
        catch (Exception ex)
        {
            MessageBox.Show(string.Format(Strings.Msg_ExportFailed, ex.Message), "ContextBridge", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }
        bool hasLocalIcons = doc.Commands.Any(c =>
            c.Icon.Source == IconSources.File &&
            c.Icon.Path is not null &&
            c.Icon.Path.Contains(@"ContextBridge\icons\", StringComparison.OrdinalIgnoreCase));
        StatusText = hasLocalIcons
            ? string.Format(Strings.Status_ExportedLocalIcons, doc.Commands.Count, dlg.FileName)
            : string.Format(Strings.Status_Exported, doc.Commands.Count, dlg.FileName);
    }

    [RelayCommand]
    void Import() => ImportFile(null);

    public void ImportFile(string? path)
    {
        if (path is null)
        {
            var dlg = new OpenFileDialog
            {
                Filter = $"{Strings.Filter_Share} (*{ShareDocument.FileSuffix};*.json)|*.json|{Strings.Filter_AllFiles} (*.*)|*.*",
            };
            if (dlg.ShowDialog() != true) return;
            path = dlg.FileName;
        }
        ImportPlan plan;
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists) throw new FileNotFoundException(Strings.Err_FileNotFound, path);
            if (info.Length > 4 * 1024 * 1024) throw new FormatException(Strings.Err_FileTooLarge);
            var doc = ShareImporter.Parse(File.ReadAllText(path));
            plan = ShareImporter.Plan(doc, _config, Locator.Exists);
        }
        catch (Exception ex)
        {
            MessageBox.Show(string.Format(Strings.Msg_ImportFailed, ex.Message), "ContextBridge", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (plan.Items.Count == 0)
        {
            MessageBox.Show(Strings.Msg_NoCommandsInFile, "ContextBridge");
            return;
        }
        // Always show what will be launched, never import without approval.
        var win = new ImportConfirmWindow(plan, Path.GetFileName(path)) { Owner = Application.Current.MainWindow };
        if (win.ShowDialog() != true) return;
        int before = _config.Commands.Count;
        plan.ApplyTo(_config, Path.GetFileName(path));
        if (_config.Commands.Count == before) return;
        var first = _config.Commands[before];
        Rebuild(first);
        MarkDirty();
        StatusText = string.Format(Strings.Status_Imported, _config.Commands.Count - before);
    }

    [RelayCommand]
    void OpenDataFolder()
    {
        try
        {
            Directory.CreateDirectory(_store.Directory);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"\"{_store.Directory}\"") { UseShellExecute = true })?.Dispose();
        }
        catch (Exception ex)
        {
            MessageBox.Show(string.Format(Strings.Msg_OpenFolderFailed, ex.Message), "ContextBridge", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    // ---------------------------------------------------------------- package registration

    public void RefreshPackageStatus()
    {
        var s = PackageService.Query();
        (PackageMessage, PackageAction, PackageNeedsAttention) = s.State switch
        {
            PackageState.Registered => (string.Format(Strings.Pkg_Registered, s.InstalledVersion), Strings.Pkg_ActionReregister, false),
            PackageState.Outdated => (string.Format(Strings.Pkg_Outdated, s.InstalledVersion), Strings.Pkg_ActionUpdate, true),
            PackageState.NotRegistered => (Strings.Pkg_NotRegistered, Strings.Pkg_ActionRegister, true),
            _ => (Strings.Pkg_DevBuild, "", true),
        };
    }

    [RelayCommand(CanExecute = nameof(HasPackageAction))]
    async Task RegisterPackage()
    {
        if (string.IsNullOrEmpty(PackageAction)) return;
        StatusText = Strings.Status_Registering;
        var error = await PackageService.RunScriptAsync("Register");
        RefreshPackageStatus();
        if (error is null)
        {
            StatusText = Strings.Status_Registered;
            // A freshly (re)registered handler is picked up reliably only by a new Explorer process.
            if (MessageBox.Show(Strings.Msg_RestartAfterRegister, "ContextBridge", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
                await RestartExplorerCore(afterSave: false);
        }
        else
        {
            StatusText = Strings.Status_RegisterFailed;
            MessageBox.Show(string.Format(Strings.Msg_RegisterFailed, error), "ContextBridge", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
}
