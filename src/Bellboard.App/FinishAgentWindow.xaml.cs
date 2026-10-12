using System;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using Bellboard.Core;

namespace Bellboard;

/// <summary>Merge, open a PR for, keep, or discard an agent's branch and worktree.</summary>
public partial class FinishAgentWindow : Window
{
    private readonly MuxController _mux;
    private readonly Workspace _ws;
    private readonly AgentTask _task;
    private AgentSummary? _summary;
    private bool _confirmDiscard;

    public FinishAgentWindow(Window owner, MuxController mux, Workspace ws)
    {
        InitializeComponent();
        Owner = owner;
        _mux = mux;
        _ws = ws;
        _task = ws.AgentTask!;
        TaskText.Text = _task.Task;
        BranchText.Text = $"{_task.Branch}  →  {_task.Base}\n{_task.Worktree}";
        MergeButton.Content = $"Merge into {_task.Base}";
        SetBusy(true);
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) { Close(); e.Handled = true; } };
        Loaded += async (_, _) => await LoadSummaryAsync();
    }

    private async Task LoadSummaryAsync()
    {
        try
        {
            var s = _summary = await _mux.Agents.InspectAsync(_task);
            var lines = new System.Collections.Generic.List<string>
            {
                s.Ahead == 0 ? "No commits on the branch yet." : $"{s.Ahead} commit(s){(s.DiffStat != null ? " · " + s.DiffStat : "")}",
            };
            if (s.Dirty.Count > 0) lines.Add($"{s.Dirty.Count} uncommitted change(s) in the worktree: {string.Join(", ", s.Dirty.Take(4).Select(d => d.Length > 3 ? d[3..] : d))}{(s.Dirty.Count > 4 ? ", …" : "")}");
            if (s.RepoBranch != _task.Base) lines.Add($"The main checkout is on '{s.RepoBranch}', not '{_task.Base}', so merging is disabled.");
            else if (s.RepoDirty) lines.Add("The main checkout has uncommitted changes, so merging is disabled.");
            if (!s.GhAvailable) lines.Add("GitHub CLI (gh) isn't installed, so pull requests are disabled.");
            else if (!s.HasRemote) lines.Add("No 'origin' remote, so pull requests are disabled.");
            SummaryText.Text = string.Join("\n", lines);
            SetBusy(false);
            MergeButton.IsEnabled = s.RepoBranch == _task.Base && !s.RepoDirty && s.Dirty.Count == 0;
            PrButton.IsEnabled = s.GhAvailable && s.HasRemote && s.Ahead > 0 && s.Dirty.Count == 0;
        }
        catch (Exception ex)
        {
            SummaryText.Text = "Couldn't inspect the branch.";
            ShowError(ex.Message);
            SetBusy(false);
            MergeButton.IsEnabled = PrButton.IsEnabled = false;
        }
    }

    private void SetBusy(bool busy)
    {
        foreach (var b in new[] { MergeButton, PrButton, KeepButton, DiscardButton }) b.IsEnabled = !busy;
    }

    private async Task RunAsync(string mode, bool force = false)
    {
        SetBusy(true);
        try
        {
            var result = await _mux.Agents.FinishAsync(_ws, mode, force);
            var anchor = _mux.Selected?.FocusedPane;
            if (anchor != null) _mux.Notifications.Post(anchor, "Agent finished", result, "Bellboard");
            Close();
        }
        catch (Exception ex)
        {
            ShowError(ex.Message);
            SetBusy(false);
        }
    }

    private void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorText.Visibility = Visibility.Visible;
    }

    private async void Merge_Click(object sender, RoutedEventArgs e) => await RunAsync("merge");
    private async void Pr_Click(object sender, RoutedEventArgs e) => await RunAsync("pr");
    private async void Keep_Click(object sender, RoutedEventArgs e) => await RunAsync("keep");

    private async void Discard_Click(object sender, RoutedEventArgs e)
    {
        // Two-step: the first click turns the button into an explicit confirmation.
        if (!_confirmDiscard)
        {
            _confirmDiscard = true;
            var lose = _summary is { } s && (s.Ahead > 0 || s.Dirty.Count > 0)
                ? $"Click again to delete {s.Ahead} commit(s) and {s.Dirty.Count} change(s)"
                : "Click again to discard";
            DiscardButton.Content = lose;
            return;
        }
        await RunAsync("discard", force: true);
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => Close();
}
