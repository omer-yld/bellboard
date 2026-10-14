using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Windows.Threading;
using Bellboard.Core;
using Bellboard.Ipc;

namespace Bellboard.Services;

/// <summary>
/// Pushes Bellboard's changes to pipe subscribers (`events.subscribe`): the remote sidecar and `bell events`.
/// Topics:
///   state  — the whole phone-view state (StateJson.Remote), at most every 200 ms, after anything changed;
///   notice — a notification was posted;
///   prompt — a permission request is waiting for an answer, or was answered;
///   remote — device changes for the sidecar (revoked, settings).
/// Everything runs on the UI thread.
/// </summary>
public sealed class EventHub
{
    public static readonly string[] Topics = ["state", "notice", "prompt", "remote"];

    private sealed record Subscriber(PipeConnection Conn, string Sub, HashSet<string> Topics);

    private readonly MuxController _mux;
    private readonly List<Subscriber> _subs = new();
    private readonly DispatcherTimer _debounce;
    private bool _dirty;

    public EventHub(MuxController mux, Dispatcher ui)
    {
        _mux = mux;
        _debounce = new DispatcherTimer(TimeSpan.FromMilliseconds(200), DispatcherPriority.Background, (_, _) => FlushState(), ui) { IsEnabled = false };
        Workspace.Changed += _ => MarkDirty();
        mux.Workspaces.CollectionChanged += (_, _) => MarkDirty();
    }

    public bool HasSubscribers(string topic) => _subs.Any(s => s.Topics.Contains(topic));

    public string Subscribe(PipeConnection conn, IEnumerable<string> topics)
    {
        var set = new HashSet<string>(topics.Where(t => Topics.Contains(t)));
        if (set.Count == 0) throw new InvalidOperationException($"no known topics (choose from {string.Join(", ", Topics)})");
        Subscriber? me = null;
        var sub = conn.AddSubscription(() => _subs.Remove(me!));
        me = new Subscriber(conn, sub, set);
        _subs.Add(me);
        return sub;
    }

    /// <summary>Something the state shows changed: send a fresh state soon.</summary>
    public void MarkDirty()
    {
        if (_dirty || !HasSubscribers("state")) return;
        _dirty = true;
        _debounce.Start();
    }

    private void FlushState()
    {
        _debounce.Stop();
        _dirty = false;
        if (!HasSubscribers("state")) return;
        JsonObject state;
        try { state = StateJson.Remote(_mux); }
        catch (Exception ex) { Log.Error("building remote state", ex); return; }
        Publish("state", "state", state);
    }

    public void Publish(string topic, string ev, JsonNode? data)
    {
        foreach (var s in _subs.Where(s => s.Topics.Contains(topic)).ToList())
            s.Conn.Push(s.Sub, ev, data?.DeepClone());
    }
}
