# Third-party notices

Wisp is MIT-licensed (see `LICENSE`). It's built on these open-source components, each under its own license.

## Libraries shipped with Wisp

| Component | License | Project |
|---|---|---|
| .NET runtime, WPF, Windows Forms | MIT | https://github.com/dotnet/runtime |
| Anthropic C# SDK | MIT | https://github.com/anthropics/anthropic-sdk-csharp |
| Microsoft.Data.Sqlite, Microsoft.Extensions.AI.Abstractions, System.* packages | MIT | https://github.com/dotnet |
| SQLitePCLRaw (bundles SQLite, public domain) | Apache-2.0 | https://github.com/ericsink/SQLitePCL.raw |
| Whisper.net and its runtimes (bundles whisper.cpp, MIT) | MIT | https://github.com/sandrohanea/whisper.net |
| NAudio | MIT | https://github.com/naudio/NAudio |
| FlaUI | MIT | https://github.com/FlaUI/FlaUI |
| Interop.UIAutomationClient | MIT | https://github.com/Roemer/UIAutomation-Interop |
| ManagedShell | Apache-2.0 | https://github.com/cairoshell/ManagedShell |
| Velopack | MIT | https://github.com/velopack/velopack |

## Downloaded on first run (not shipped)

| Component | License | Source |
|---|---|---|
| Whisper speech model (large-v3-turbo, GGML) | MIT | OpenAI Whisper, via Hugging Face |

## Artwork

The wallpapers in `src/Wisp/Wallpapers` are from the GNOME project's [gnome-backgrounds](https://gitlab.gnome.org/GNOME/gnome-backgrounds), licensed under [CC BY-SA 3.0 US](https://creativecommons.org/licenses/by-sa/3.0/us/). Wisp ships them center-cropped and scaled to 2560x1440; the same license applies to those versions. Individual credits are in `src/Wisp/Wallpapers/CREDITS.md`.

The full license texts of the libraries are included in their packages and at the project links above.
