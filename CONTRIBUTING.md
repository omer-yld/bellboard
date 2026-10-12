# Contributing to Bellboard

Thanks for helping. Bug reports, ideas and pull requests are all welcome.

## Reporting a bug

Open an [issue](https://github.com/omer-yld/bellboard/issues/new/choose) and attach the bundle from
`bell doctor --zip` (versions, settings and the log, which can name your folders, so look it over first;
terminal contents are only included with `--include-session`). Say what you did,
what you expected and what happened. A screenshot or `bell screenshot -o shot.png` helps.

For security problems, don't open an issue: see [SECURITY.md](SECURITY.md).

## Building

You need Windows 10 or 11, the [.NET 9 SDK](https://dotnet.microsoft.com/download/dotnet/9.0) and the
WebView2 Runtime (already on Windows 11).

```powershell
git clone https://github.com/omer-yld/bellboard
cd bellboard
.\build.ps1 -Test -Smoke      # unit tests, build into .\dist\Bellboard, end-to-end smoke test
.\dist\Bellboard\Bellboard.exe
```

`.\build.ps1 -Install` installs your build for your user, like a release would.

### Working beside your own Bellboard

The smoke test and your experiments shouldn't touch the Bellboard you use. Two variables give a build its
own pipe and data folder:

```powershell
$env:BELLBOARD_PIPE = "bellboard-dev"
$env:BELLBOARD_HOME = "$env:TEMP\bellboard-dev"
.\dist\Bellboard\Bellboard.exe --fresh
.\dist\Bellboard\bell.exe list      # talks to the dev instance (same variables)
```

`bell screenshot -o x.png` captures the window, which makes UI changes easy to check.

## Layout

| Path | What's there |
|---|---|
| `src/Bellboard.App` | The WPF app: `MainWindow`, the split tree (`Core/MuxController.cs`, `Core/PaneStack.cs`), panes (`Panes/`), terminals (`Terminal/`: ConPTY, shells, shell integration), services (settings, session, notifications), the pipe API (`Ipc/CommandRouter.cs`) and the web pages for terminals and the start page (`web/`) |
| `src/Bellboard.Cli` | `bell.exe`: the CLI, agent hooks (`Hooks.cs`), the MCP server (`McpServer.cs`), update and doctor |
| `src/Shared` | What the app and the CLI share: the pipe protocol, environment variable names, data folders |
| `tests/Bellboard.Tests` | Unit tests (xUnit) |
| `tests/smoke.ps1` | End-to-end test: starts an isolated instance and drives it through the CLI |
| `packaging/` | `install.ps1` and `uninstall.ps1` |

## Pull requests

- Keep a pull request to one change, and say why it's needed.
- Run `.\build.ps1 -Test -Smoke` before you push; CI runs the same on `windows-latest`.
- Add a unit test for new logic. Pure logic (layout math, parsing, CLI arguments) lives in classes that
  tests can reach without WPF.
- Match the surrounding code: its naming, comment density and idioms.
- UI text is plain and short. A button says what it does; an error says what went wrong and how to fix it.
- Anything the CLI or the pipe API can do should stay limited to the current Windows user.

By contributing you agree that your contribution is licensed under the [MIT License](LICENSE).

## Code of conduct

Be kind and assume good intent. See [CODE_OF_CONDUCT.md](CODE_OF_CONDUCT.md).
