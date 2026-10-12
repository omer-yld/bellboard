using System;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using Bellboard.Services;
using Bellboard.Shared;

namespace Bellboard;

public partial class App : Application
{
    public static bool DevMode { get; private set; }
    private Mutex? _singleInstance;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DevMode = e.Args.Contains("--dev") || Environment.GetEnvironmentVariable("BELLBOARD_DEV") == "1";
        DispatcherUnhandledException += OnUnhandled;
        AppDomain.CurrentDomain.UnhandledException += (_, a) => Log.Error("unhandled (domain)", a.ExceptionObject as Exception);

        var pipe = BellboardProtocol.ServerPipeName();
        _singleInstance = new Mutex(true, @"Local\" + pipe, out bool first);
        if (!first)
        {
            // Already running: bring that window forward (optionally opening a workspace) and exit.
            using (var client = PipeClient.TryConnect(pipe, 2000))
            {
                try { client?.Call("app.activate"); } catch { }
            }
            Shutdown();
            return;
        }

        var moved = LegacyData.Migrate();
        Log.Info($"Bellboard {BellboardProtocol.Version} starting (pid {Environment.ProcessId}, Windows build {Environment.OSVersion.Version.Build})");
        foreach (var note in moved) Log.Info("rename from wmux: " + note);
        var window = new MainWindow(BellboardConfig.Load(), pipe, noRestore: e.Args.Contains("--fresh"));
        MainWindow = window;
        window.Show();
    }

    private void OnUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log.Error("unhandled", e.Exception);
        e.Handled = true;
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try { _singleInstance?.ReleaseMutex(); } catch { }
        _singleInstance?.Dispose();
        base.OnExit(e);
    }
}
