# Third-party notices

Bellboard is MIT-licensed ([LICENSE](LICENSE)). It ships with the following third-party components,
each under its own license.

| Component | Used for | License | Where |
|---|---|---|---|
| [xterm.js](https://github.com/xtermjs/xterm.js) and its addons (fit, search, serialize, unicode11, web-links, webgl) | The terminal renderer | MIT, © The xterm.js authors | [`src/Bellboard.App/web/vendor/LICENSE-xterm.txt`](src/Bellboard.App/web/vendor/LICENSE-xterm.txt) |
| [Microsoft.Windows.Console.ConPTY](https://www.nuget.org/packages/Microsoft.Windows.Console.ConPTY) (conpty.dll, OpenConsole.exe) | The pseudoconsole terminals run in | MIT, © Microsoft Corporation | NuGet package |
| [Microsoft.Web.WebView2](https://www.nuget.org/packages/Microsoft.Web.WebView2) SDK | Hosting terminals and the browser pane | BSD-style license, © Microsoft Corporation | `LICENSE.txt` in the NuGet package |
| [Microsoft.Toolkit.Uwp.Notifications](https://www.nuget.org/packages/Microsoft.Toolkit.Uwp.Notifications) | Windows notifications | MIT, © .NET Foundation and Contributors | NuGet package |
| [Geist](https://github.com/vercel/geist-font) | The interface font | SIL Open Font License 1.1, © The Geist Project Authors | [`src/Bellboard.App/fonts/Geist-OFL.txt`](src/Bellboard.App/fonts/Geist-OFL.txt) |
| [JetBrains Mono](https://github.com/JetBrains/JetBrainsMono) | The fallback terminal and code font | SIL Open Font License 1.1, © The JetBrains Mono Project Authors | [`src/Bellboard.App/fonts/JetBrainsMono-OFL.txt`](src/Bellboard.App/fonts/JetBrainsMono-OFL.txt) |
| [.NET runtime](https://github.com/dotnet/runtime) (self-contained builds only) | Running the app without a .NET install | MIT, © .NET Foundation and Contributors | Included in the release zip |

The WebView2 Runtime itself isn't bundled: Bellboard uses the one installed with Windows (or from
Microsoft). The icons in the interface come from Windows' own Segoe Fluent Icons font, which isn't
redistributed.
