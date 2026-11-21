using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using System.Globalization;
using ContextBridge.App.Resources;
using ContextBridge.App.Services;
using ContextBridge.Core;

namespace ContextBridge.App.ViewModels;

/// <summary>Editable wrapper over a <see cref="CommandDef"/>; writes straight through to the model.</summary>
sealed partial class CommandViewModel : ObservableObject
{
    readonly MainViewModel _owner;
    public CommandDef Model { get; }

    public CommandViewModel(CommandDef model, MainViewModel owner)
    {
        Model = model;
        _owner = owner;
    }

    void Changed(params string[] extra)
    {
        foreach (var p in extra) OnPropertyChanged(p);
        OnPropertyChanged(nameof(DisplayTitle));
        OnPropertyChanged(nameof(TargetSummary));
        OnPropertyChanged(nameof(Subtitle));
        OnPropertyChanged(nameof(Preview));
        OnPropertyChanged(nameof(GroupTitle));
        OnPropertyChanged(nameof(AutoTitleExample));
        _owner.OnCommandEdited(this);
    }

    public string Id => Model.Id;

    public string DisplayTitle => Template.DisplayTitle(Model);

    /// <summary>The title the menu would show if Title were blank (Windows display language, like the handler).</summary>
    public string AutoTitleExample
    {
        get
        {
            var stem = System.IO.Path.GetFileNameWithoutExtension(Template.ExpandProgram(Model.Action.Program ?? "").Replace('/', '\\'));
            return Template.AutoTitle(string.IsNullOrEmpty(stem) ? "Notepad" : stem, CultureInfo.InstalledUICulture);
        }
    }

    public string Title
    {
        get => Model.Title;
        set { if (Model.Title != value) { Model.Title = value; Changed(nameof(Title)); } }
    }

    public bool Enabled
    {
        get => Model.Enabled;
        set { if (Model.Enabled != value) { Model.Enabled = value; Changed(nameof(Enabled)); } }
    }

    public string GroupId
    {
        get => Model.Group ?? "";
        set
        {
            if (_owner.IsRebuildingGroups) return;
            string? v = string.IsNullOrEmpty(value) ? null : value;
            if (Model.Group != v) { Model.Group = v; Changed(nameof(GroupId)); }
        }
    }

    public string GroupTitle => _owner.GroupTitleOf(Model.Group);

    public string Subtitle => string.IsNullOrEmpty(GroupTitle) ? TargetSummary : $"{GroupTitle}  {TargetSummary}";

    bool HasType(string t) => Model.Target.ItemTypes.Contains(t) || Model.Target.ItemTypes.Contains(ItemTypes.All);

    void SetType(string t, bool on)
    {
        var list = Model.Target.ItemTypes;
        if (list.Contains(ItemTypes.All))
        {
            list.Clear();
            list.AddRange(ItemTypes.Known.Where(k => k != ItemTypes.All));
        }
        if (on && !list.Contains(t)) list.Add(t);
        if (!on) list.Remove(t);
        // Keep canonical order.
        var ordered = ItemTypes.Known.Where(list.Contains).ToList();
        list.Clear();
        list.AddRange(ordered);
        Changed(nameof(ForFiles), nameof(ForFolders), nameof(ForBackground), nameof(ForDrives));
    }

    public bool ForFiles { get => HasType(ItemTypes.File); set => SetType(ItemTypes.File, value); }
    public bool ForFolders { get => HasType(ItemTypes.Folder); set => SetType(ItemTypes.Folder, value); }
    public bool ForBackground { get => HasType(ItemTypes.FolderBackground); set => SetType(ItemTypes.FolderBackground, value); }
    public bool ForDrives { get => HasType(ItemTypes.Drive); set => SetType(ItemTypes.Drive, value); }

    public string Extensions
    {
        get => Model.Target.Extensions is { Count: > 0 } e ? string.Join(", ", e) : "";
        set
        {
            var raw = value.Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (raw.Any(x => x is "*" or ".*"))
            {
                Model.Target.Extensions = null;
            }
            else
            {
                var list = raw
                    .Select(x => x.StartsWith('.') ? x.ToLowerInvariant() : "." + x.ToLowerInvariant())
                    .Distinct().ToList();
                Model.Target.Extensions = list.Count == 0 ? null : list;
            }
            // Do not raise Extensions itself: the box updates while typing and rewriting its
            // text (normalizing ".txt, md") would move the caret. Normalized form shows on reselect.
            Changed();
        }
    }

    public int MinSelection
    {
        get => Model.Target.MinSelection;
        set { Model.Target.MinSelection = Math.Max(0, value); Changed(nameof(MinSelection)); }
    }

    public int MaxSelection
    {
        get => Model.Target.MaxSelection;
        set { Model.Target.MaxSelection = Math.Max(0, value); Changed(nameof(MaxSelection)); }
    }

    public string Program
    {
        get => Model.Action.Program;
        set
        {
            if (Model.Action.Program == value) return;
            Model.Action.Program = value;
            _owner.InvalidateProgramCache(value);
            Changed(nameof(Program), nameof(Icon), nameof(ProgramStatus), nameof(ProgramMissing));
        }
    }

    public string Arguments
    {
        get => Model.Action.Arguments;
        set { if (Model.Action.Arguments != value) { Model.Action.Arguments = value; Changed(nameof(Arguments)); } }
    }

    public string WorkingDirectory
    {
        get => Model.Action.WorkingDirectory ?? "";
        set
        {
            var v = string.IsNullOrWhiteSpace(value) ? null : value;
            if (Model.Action.WorkingDirectory != v) { Model.Action.WorkingDirectory = v; Changed(nameof(WorkingDirectory)); }
        }
    }

    public bool LaunchEach
    {
        get => Model.Action.MultiFileMode == MultiFileModes.Each;
        set { Model.Action.MultiFileMode = value ? MultiFileModes.Each : MultiFileModes.Single; Changed(nameof(LaunchEach)); }
    }

    public bool RunAsAdmin
    {
        get => Model.Action.RunAsAdmin;
        set { Model.Action.RunAsAdmin = value; Changed(nameof(RunAsAdmin)); }
    }

    public string Window
    {
        get => Model.Action.Window;
        set { Model.Action.Window = value; Changed(nameof(Window)); }
    }

    public string IconSource
    {
        get => Model.Icon.Source;
        set { Model.Icon.Source = value; Changed(nameof(IconSource), nameof(IconEnabled), nameof(Icon)); }
    }

    public bool IconEnabled => Model.Icon.Source != IconSources.None;

    public string IconPath
    {
        get => Model.Icon.Path ?? "";
        set
        {
            var old = Model.Icon.Path;
            var v = string.IsNullOrWhiteSpace(value) ? null : value;
            if (old == v) return;
            Model.Icon.Path = v;
            if (v is not null) IconLoader.Invalidate(Template.ExpandProgram(v));
            Changed(nameof(IconPath), nameof(Icon));
        }
    }

    public int IconIndex
    {
        get => Model.Icon.Index;
        set { if (Model.Icon.Index != value) { Model.Icon.Index = value; Changed(nameof(IconIndex), nameof(Icon)); } }
    }

    public ImageSource? Icon => IconLoader.For(Model, _owner.Locator);

    public string ProgramStatus
    {
        get
        {
            if (string.IsNullOrWhiteSpace(Model.Action.Program)) return "";
            var (resolved, path) = _owner.TryGetLocatedProgram(Model.Action.Program);
            if (!resolved) return Strings.Prog_Checking;
            return path is null ? Strings.Prog_NotFound : "✓ " + path;
        }
    }

    public bool ProgramMissing
    {
        get
        {
            if (string.IsNullOrWhiteSpace(Model.Action.Program)) return false;
            var (resolved, path) = _owner.TryGetLocatedProgram(Model.Action.Program);
            return resolved && path is null;
        }
    }

    public void RefreshProgramStatus()
    {
        OnPropertyChanged(nameof(ProgramStatus));
        OnPropertyChanged(nameof(ProgramMissing));
        OnPropertyChanged(nameof(Icon));
    }

    public string OriginSummary => Model.Origin?.Type switch
    {
        "legacy" => string.Format(Strings.Origin_Legacy, string.Join(", ", Model.Origin.Keys ?? new())),
        "import" => string.Format(Strings.Origin_Import, Model.Origin.Source),
        _ => "",
    };

    public string TargetSummary
    {
        get
        {
            var parts = new List<string>();
            if (ForFiles) parts.Add(Model.Target.Extensions is { Count: > 0 } e ? string.Join(" ", e) : Strings.Target_Files);
            if (ForFolders) parts.Add(Strings.Target_Folders);
            if (ForBackground) parts.Add(Strings.Target_BackgroundShort);
            if (ForDrives) parts.Add(Strings.Target_Drives);
            return string.Join(" / ", parts);
        }
    }

    /// <summary>What would run for a representative selection (same planner as "Try it").</summary>
    public string Preview
    {
        get
        {
            var (sel, label) = SampleSelection();
            if (sel is null) return Strings.Prev_ChooseTypes;
            var plans = LaunchPlanner.Plan(Model, sel, Template.ProcessEnv);
            var sb = new System.Text.StringBuilder();
            sb.AppendLine(string.Format(Strings.Prev_Example, label));
            foreach (var p in plans.Take(3))
                sb.AppendLine($"> {Template.QuoteArg(p.Program)} {p.Arguments}".TrimEnd());
            if (plans.Count > 3) sb.AppendLine(string.Format(Strings.Prev_More, plans.Count - 3));
            sb.Append(string.Format(Strings.Prev_WorkingDir, plans[0].WorkingDirectory));
            if (RunAsAdmin) sb.Append(Strings.Prev_AsAdmin);
            return sb.ToString();
        }
    }

    (SelectionInfo? Selection, string Label) SampleSelection()
    {
        const string dir = @"C:\Users\you\Documents";
        if (ForFiles)
        {
            var ext = Model.Target.Extensions is { Count: > 0 } e ? e[0] : ".txt";
            int n = Math.Max(Model.Target.MinSelection, Model.Target.MaxSelection == 1 ? 1 : 2);
            n = Math.Clamp(n, 1, 5);
            var items = Enumerable.Range(1, n).Select(i => new SelectedEntry($@"{dir}\sample {i}{ext}", SelectedKind.File)).ToList();
            return (new SelectionInfo(items), n == 1 ? Strings.Prev_SampleFile : string.Format(Strings.Prev_SampleFiles, n));
        }
        if (ForFolders) return (new SelectionInfo(new[] { new SelectedEntry(dir + @"\Project", SelectedKind.Folder) }), Strings.Prev_SampleFolder);
        if (ForBackground) return (SelectionInfo.Background(dir), Strings.Prev_SampleBackground);
        if (ForDrives) return (new SelectionInfo(new[] { new SelectedEntry(@"D:\", SelectedKind.Drive) }), Strings.Prev_SampleDrive);
        return (null, "");
    }

    // ---- validation markers shown in the list ----

    string? _issueText;
    bool _hasError;

    /// <summary>Validation messages for this command (null when none).</summary>
    public string? IssueText => _issueText;
    public bool HasIssue => _issueText is not null;
    public bool HasError => _hasError;

    public void SetIssues(IReadOnlyList<ValidationIssue> issues)
    {
        string? text = issues.Count == 0 ? null : string.Join("\n", issues.Select(i => i.Message));
        bool err = issues.Any(i => i.Severity == Severity.Error);
        if (text == _issueText && err == _hasError) return;
        _issueText = text;
        _hasError = err;
        OnPropertyChanged(nameof(IssueText));
        OnPropertyChanged(nameof(HasIssue));
        OnPropertyChanged(nameof(HasError));
    }

    public void RefreshAll() => OnPropertyChanged(string.Empty);
}
