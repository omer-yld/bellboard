using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using Bellboard.Core;
using Bellboard.Terminal;

namespace Bellboard;

/// <summary>
/// "New agent": where it runs and on which repo, the task, which agent (with what's installed there), the base
/// and new branch, and a preview of the commands it will run.
/// </summary>
public partial class NewAgentWindow : Window
{
    /// <summary>An option in the "Runs in" switch (Id = the repo path to use) or an agent card (Id = claude, codex…).</summary>
    public sealed class Choice : INotifyPropertyChanged
    {
        public required string Id { get; init; }
        public required string Label { get; init; }
        public string Name => Label;
        public string Mono { get; init; } = "";
        public bool IsCustom => Id == "custom";

        private bool _selected;
        public bool IsSelected
        {
            get => _selected;
            set { _selected = value; Changed(nameof(IsSelected)); }
        }

        private bool? _installed;
        /// <summary>null while checking.</summary>
        public bool? Installed
        {
            get => _installed;
            set
            {
                _installed = value;
                Changed(nameof(Installed));
                Changed(nameof(Status));
                Changed(nameof(StatusBrush));
                Changed(nameof(Missing));
            }
        }

        public string Status => IsCustom ? "Any CLI" : Installed switch { true => "Installed", false => "Not found", null => "Checking…" };
        public Brush StatusBrush => (Brush)Application.Current.Resources[IsCustom || Installed == true ? "Green" : "Faint"];
        public bool Missing => !IsCustom && Installed == false;

        public event PropertyChangedEventHandler? PropertyChanged;
        private void Changed(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    private static readonly Brush CommandBrush = Frozen(0x7F, 0xB6, 0xFF), FlagBrush = Frozen(0xC9, 0x9B, 0xF0),
        PathBrush = Frozen(0x62, 0xC7, 0xC2), StringBrush = Frozen(0xE9, 0xC2, 0x6A);

    private readonly MuxController _mux;
    private readonly List<Choice> _envs = new();
    private readonly List<Choice> _agents;
    private (string Root, string Head)? _repoInfo;
    private int _check;
    private bool _checking, _branchEdited, _settingBranch, _settingEnvs;

    /// <param name="remote">The SSH host of the focused pane, if any: agents can't run there.</param>
    public NewAgentWindow(Window owner, MuxController mux, string? repo, string? initialTask = null, string? remote = null)
    {
        InitializeComponent();
        Owner = owner;
        _mux = mux;
        _agents = new()
        {
            new Choice { Id = "claude", Label = "Claude Code", Mono = "CC", IsSelected = true },
            new Choice { Id = "codex", Label = "Codex", Mono = "CX" },
            new Choice { Id = "opencode", Label = "OpenCode", Mono = "OC" },
            new Choice { Id = "pi", Label = "Pi", Mono = "PI" },
            new Choice { Id = "custom", Label = "Custom command", Mono = ">_" },
        };
        AgentGrid.ItemsSource = _agents;
        TaskBox.Text = initialTask ?? "";
        if (remote != null)
        {
            ShowError($"This pane is connected to {remote} over SSH. Agents run on this PC or in WSL, so pick a repo here with the Repo button.");
            repo = null;
        }
        SetRepo(repo);
        Loaded += (_, _) => { TaskBox.Focus(); Keyboard.Focus(TaskBox); TaskBox.CaretIndex = TaskBox.Text.Length; };
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) { Close(); e.Handled = true; }
            else if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.Control) { Start_Click(this, new RoutedEventArgs()); e.Handled = true; }
        };
    }

    private static Brush Frozen(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }

    private Choice SelectedAgent => _agents.First(a => a.IsSelected);
    private string? EnvRepo => _envs.FirstOrDefault(e => e.IsSelected)?.Id;
    private bool InWsl => EnvRepo != null && ShellResolver.ParseWslPath(EnvRepo) != null;

    /// <summary>Offers the repo's own environment, plus WSL for a repo on a Windows drive (through /mnt/c/...).</summary>
    private void SetRepo(string? repo)
    {
        _settingEnvs = true;
        _envs.Clear();
        if (repo != null)
        {
            if (ShellResolver.ParseWslPath(repo) is { } wsl)
            {
                _envs.Add(new Choice { Id = repo, Label = "WSL · " + wsl.Distro });
            }
            else
            {
                _envs.Add(new Choice { Id = repo, Label = "Windows" });
                if (WindowsToLinux(repo) is { } linux)
                    foreach (var distro in ShellResolver.WslDistros().Take(2))
                        _envs.Add(new Choice { Id = ShellResolver.WslUncPath(distro, linux), Label = "WSL · " + distro });
            }
            _envs[0].IsSelected = true;
        }
        EnvList.ItemsSource = null;
        EnvList.ItemsSource = _envs;
        RepoText.Text = repo == null ? "Choose a repository" : ShellResolver.ParseWslPath(repo)?.LinuxPath ?? repo;
        _settingEnvs = false;
        _ = CheckAsync();
    }

    /// <summary>C:\code\app → /mnt/c/code/app (WSL's default mount); null for anything else.</summary>
    internal static string? WindowsToLinux(string path)
    {
        var m = Regex.Match(path, @"^([A-Za-z]):\\?(.*)$");
        if (!m.Success) return null;
        var rest = m.Groups[2].Value.Replace('\\', '/').Trim('/');
        return $"/mnt/{char.ToLowerInvariant(m.Groups[1].Value[0])}" + (rest.Length > 0 ? "/" + rest : "");
    }

    /// <summary>Finds the repo root and which agents are installed where the agent would run.</summary>
    private async Task CheckAsync()
    {
        var version = ++_check;
        _repoInfo = null;
        _checking = EnvRepo != null;
        foreach (var a in _agents.Where(a => !a.IsCustom)) a.Installed = EnvRepo == null ? false : null;
        UpdatePlan();
        if (EnvRepo is not { } dir) return;
        var info = AgentManager.InspectRepoAsync(dir);
        var installed = AgentManager.InstalledAgentsAsync(dir);
        await Task.WhenAll(info, installed);
        if (version != _check) return;
        _checking = false;
        _repoInfo = info.Result;
        foreach (var a in _agents.Where(a => !a.IsCustom)) a.Installed = installed.Result.Contains(a.Id);
        BaseBox.Tag = _repoInfo is { } r ? $"{r.Head} (current)" : "current branch";
        UpdatePlan();
    }

    private void UpdatePlan()
    {
        var task = TaskBox.Text.Trim();
        TaskPlaceholder.Visibility = TaskBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        CountText.Text = task.Length > 0 ? $"{task.Length} characters" : "";
        if (!_branchEdited)
        {
            _settingBranch = true;
            BranchBox.Text = task.Length > 0 ? "bellboard/" + AgentManager.Slug(task) : "";
            _settingBranch = false;
        }

        var agent = SelectedAgent;
        CustomBox.Visibility = agent.IsCustom ? Visibility.Visible : Visibility.Collapsed;
        var branch = BranchBox.Text.Trim() is { Length: > 0 } b ? b : "bellboard/<from-task>";
        var leaf = BranchBox.Text.Trim().Length > 0 ? AgentManager.Slug(branch.Split('/')[^1]) : "<from-task>";
        var root = _repoInfo?.Root ?? EnvRepo;
        string Show(string p) => ShellResolver.ParseWslPath(p)?.LinuxPath ?? p;
        var rootShown = root == null ? "<repo>" : Show(root);
        var worktree = root == null ? $"<repo>.worktrees/{leaf}" : Show(Path.Combine(AgentManager.WorktreeContainer(root), leaf));
        var baseName = BaseBox.Text.Trim() is { Length: > 0 } bb ? bb : _repoInfo?.Head ?? "<current branch>";
        var command = agent.IsCustom
            ? (CustomBox.Text.Trim() is { Length: > 0 } custom ? custom : "<your command>")
            : AgentManager.AgentCommand(agent.Id, task.Length > 0 ? (task.Length > 54 ? task[..52] + "…" : task) : "<task>", powershell: !InWsl);

        PlanText.Inlines.Clear();
        Line(1, ("git", CommandBrush), (" -C ", FlagBrush), (rootShown, PathBrush), (" worktree add ", null), (worktree, PathBrush),
            (" -b ", FlagBrush), (branch, null), (" " + baseName, null));
        Line(2, ("cd ", CommandBrush), (worktree, PathBrush));
        var space = command.IndexOf(' ');
        Line(3, (space < 0 ? command : command[..space], CommandBrush), (space < 0 ? "" : command[space..], StringBrush));

        string? problem = EnvRepo == null ? "Choose the repository the agent should work on."
            : _checking ? null
            : _repoInfo == null ? $"{Show(EnvRepo)} isn't inside a git repository."
            : agent.Missing ? $"{agent.Name} isn't installed {(InWsl ? "in " + ShellResolver.ParseWslPath(EnvRepo)!.Value.Distro : "on Windows")}. Install it there or pick another agent."
            : null;
        NoteGlyph.Text = problem != null ? "\uE7BA" : "\uEA8F";
        NoteGlyph.Foreground = (Brush)FindResource(problem != null ? "Accent2" : "Accent");
        NoteText.Inlines.Clear();
        if (problem != null)
        {
            NoteText.Inlines.Add(new Run(problem) { Foreground = (Brush)FindResource("Accent2") });
        }
        else
        {
            NoteText.Inlines.Add(new Run("Opens a workspace named "));
            NoteText.Inlines.Add(new Run(task.Length > 0 ? WorkspaceTitle(task) : "after the task") { FontWeight = FontWeights.SemiBold, Foreground = (Brush)FindResource("Muted") });
            NoteText.Inlines.Add(new Run(" and rings you when the agent needs input or finishes."));
        }
        StartButton.IsEnabled = task.Length > 0 && problem == null && !_checking
                                && (!agent.IsCustom || CustomBox.Text.Trim().Length > 0);
    }

    private static string WorkspaceTitle(string task)
    {
        var t = Regex.Replace(task.Trim(), @"\s+", " ");
        return t.Length <= 32 ? t : t[..31].TrimEnd() + "…";
    }

    private void Line(int n, params (string Text, Brush? Brush)[] parts)
    {
        if (n > 1) PlanText.Inlines.Add(new LineBreak());
        PlanText.Inlines.Add(new Run(n + "  ") { Foreground = (Brush)FindResource("Faint") });
        foreach (var (text, brush) in parts)
            PlanText.Inlines.Add(brush == null ? new Run(text) : new Run(text) { Foreground = brush });
    }

    private void Task_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e) => UpdatePlan();
    private void Plan_Changed(object sender, System.Windows.Controls.TextChangedEventArgs e) => UpdatePlan();
    private void Custom_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e) => UpdatePlan();

    private void Branch_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        if (_settingBranch) return;
        _branchEdited = BranchBox.Text.Trim().Length > 0;
        UpdatePlan();
    }

    private void Task_Focus(object sender, KeyboardFocusChangedEventArgs e) =>
        TaskFrame.BorderBrush = (Brush)FindResource(TaskBox.IsKeyboardFocused ? "Accent" : "Line2");

    private void Env_Checked(object sender, RoutedEventArgs e)
    {
        if (!_settingEnvs) _ = CheckAsync();
    }

    private void Agent_Checked(object sender, RoutedEventArgs e) => UpdatePlan();

    private void PlanToggle_Click(object sender, RoutedEventArgs e)
    {
        var show = PlanText.Visibility != Visibility.Visible;
        PlanText.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        PlanChevron.Text = show ? "\uE70E" : "\uE70D";
    }

    private void Repo_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog { Title = "Choose the repository for the agent" };
        if (_envs.FirstOrDefault()?.Id is { } current && Directory.Exists(current)) dlg.InitialDirectory = current;
        if (dlg.ShowDialog(this) == true)
        {
            ErrorText.Visibility = Visibility.Collapsed;
            SetRepo(dlg.FolderName);
        }
    }

    private async void Start_Click(object sender, RoutedEventArgs e)
    {
        UpdatePlan();
        if (!StartButton.IsEnabled || EnvRepo is not { } repo) return;
        var agent = SelectedAgent;
        StartButton.IsEnabled = false;
        StartText.Text = "Creating worktree…";
        try
        {
            await _mux.Agents.NewAsync(TaskBox.Text.Trim(), agent.IsCustom ? CustomBox.Text.Trim() : agent.Id, repo,
                BaseBox.Text.Trim() is { Length: > 0 } b ? b : null, null, null, true,
                branchName: _branchEdited ? BranchBox.Text.Trim() : null);
            Close();
        }
        catch (Exception ex)
        {
            ShowError(ex.Message);
            StartText.Text = "Start agent";
            UpdatePlan();
        }
    }

    private void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorText.Visibility = Visibility.Visible;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => Close();
}
