# Wisp

Wisp is a voice assistant and desktop for Windows 10 and 11.

- **Talk instead of typing.** Hold a key and speak: Wisp types it into any app, cleaned up, or does what you ask (open apps, search, schedule things, control windows), or answers questions about anything you circle on screen. Speech is transcribed locally on your PC.
- **A better desktop.** An optional top bar, dock and system tray replace the Windows taskbar, with themes you can customize, wallpapers, a launcher, and support for multiple monitors.
- **Your own systems.** Plugins (simple JSON files) let the assistant call your servers, home lab or scripts.

Wisp is free and open source under the MIT license.

## Download

Get the latest **`Wisp-win-Setup.exe`** from the [Releases](../../releases) page and run it. It installs for your Windows account (no admin rights needed) and adds Wisp to the Start menu. A portable zip is also available.

- **Uninstall** from Settings → Apps → Installed apps → Wisp. Uninstalling restores the Windows taskbar, theme and desktop icons if Wisp had changed them. Your settings stay in `%APPDATA%\Wisp`; delete that folder to remove everything.
- **AI features** use Claude or Gemini with your own API key, added in Settings. Read the [privacy policy](PRIVACY.md) for exactly what is sent and when. They can be switched off entirely.

## Gestures

| Hold | What happens |
|---|---|
| **Ctrl + Win** | Dictate into whatever app has focus. Speech is transcribed locally, cleaned up (filler words and self-corrections removed) and pasted when you let go. |
| **Alt + Win** | Talk to Wisp: commands, questions, scheduling, PC control. |
| **Ctrl + Shift** | Circle something on screen with the mouse, ask about it out loud, then let go. |
| **Esc** (while holding) | Cancel. |

A gesture only fires when exactly those keys are held with nothing else pressed, so normal shortcuts like Ctrl+Win+→ and Ctrl+Shift+T still work.

## The island

The pill at the top-center shows the time, and an equalizer when music is playing. Click it or scroll down on it to open the panel:

- **Home**: ask box, last reply, to-dos, now playing and volume, upcoming routines
- **Apps**: type-to-launch over every app in your Start menu, including Store apps
- **Clipboard**: searchable history; click to paste, right-click to pin or remove
- **Routines**: scheduled and repeating tasks ("every weekday at 9, open Outlook and Teams")
- **Controls**: Wisp desktop on/off, themes, customization (colors, fonts, transparency, roundness, dock and bar size), Wi-Fi and Bluetooth
- **Wallpaper**: included wallpapers, your own images, fit modes, per-screen
- **Power**: lock, sleep, restart, shut down

## Wisp desktop

Optional, and off until you turn it on in **Controls** (Wisp explains what will change and asks first):

- A **top bar** on every screen with the focused app, your tray icons (with a ^ overflow you can customize), network, volume, battery and date.
- A **dock** on every screen with pinned apps and open windows, grouped the way the Windows taskbar groups them (so each Chrome profile gets its own icon).
- The Windows **taskbar is hidden**, and Windows can be switched to your Wisp theme's dark mode and accent colors.
- Tapping **Win** can open Wisp's launcher instead of Start.

Everything is reverted when you turn it off, quit Wisp, or uninstall it.

## Wallpapers

The Wallpaper tab offers 35 included wallpapers (vector/techy designs from the GNOME project's
[gnome-backgrounds](https://gitlab.gnome.org/GNOME/gnome-backgrounds), CC BY-SA 3.0, prepared at 2560x1440;
credits in `src/Wisp/Wallpapers/CREDITS.md`), your own images (**Add image…**), wallpapers generated from the
current theme, and any folder you point it at. Fit modes: Fill (default; covers any screen size/shape),
Fit, Stretch, Center, Span. With several monitors you can apply to all screens or one.

## Architecture

```
src/Wisp
  App.xaml(.cs)          tray icon, single instance, startup
  WispController.cs      owns the services; runs dictate / talk / circle
  Input/                 low-level keyboard hook (own thread), paste/typing, clipboard watcher
  Speech/                NAudio mic capture (16 kHz mono), whisper.cpp via Whisper.net (Vulkan → CPU)
  AI/                    provider-neutral chat model; Claude (official SDK) + Gemini (REST); agent loop
  Tools/                 built-in tools, UI Automation PC control, JSON plugin host
  Core/                  settings (DPAPI-encrypted keys), SQLite store, app index, media, scheduler
  UI/                    island, circle overlay, settings, theme
```

Data lives in `%APPDATA%\Wisp`: `settings.json`, `wisp.db`, `models\`, `plugins\` and `logs\`.

## Plugins

Drop a JSON file into `%APPDATA%\Wisp\plugins`. It reloads as soon as you save. Each tool becomes `plugin_<name>_<tool>` for the assistant.

```jsonc
{
  "name": "homelab",
  "description": "My home lab",
  "enabled": true,
  "allowSelfSignedCerts": true,
  "variables": { "base": "https://10.0.0.5", "token": "${env:HOMELAB_TOKEN}" },
  "tools": [
    {
      "name": "status",
      "description": "Get service status",
      "parameters": { "service": { "type": "string", "description": "Service name" } },
      "required": ["service"],
      "http": { "method": "GET", "url": "{{base}}/api/status/{{service}}", "headers": { "Authorization": "Bearer {{token}}" } }
    },
    {
      "name": "restart",
      "description": "Restart a service",
      "parameters": { "service": { "type": "string" } },
      "risk": "confirm",
      "powershell": "Restart-Service -Name {{service}}"
    }
  ]
}
```

- **Actions**: `http` (`method`, `url`, `headers`, `body`), `powershell` (arguments are inserted as safely quoted strings), or `command` (`exe`, `args`).
- **Secrets**: `${env:NAME}` reads a value from an environment variable, so secrets stay out of the file.
- **Confirmation**: `"risk": "confirm"` makes Wisp ask in the island before running the tool.

A disabled example ships in `plugins.examples/`.

## Build

```bash
dotnet build
```

Requires the .NET 10 SDK on Windows 10 (19041) or later. The speech model (~550 MB) downloads on first run.

## Privacy

Wisp has no servers, accounts or analytics. Your voice never leaves your PC. When you use AI features, the request goes to the AI provider you chose, with your own API key. Full details: [PRIVACY.md](PRIVACY.md).

## Code signing policy

Free code signing provided by [SignPath.io](https://about.signpath.io), certificate by [SignPath Foundation](https://signpath.org).

- Committers and reviewers: [SynPrism (synprism-admin)](https://github.com/synprism-admin)
- Approvers: [SynPrism (synprism-admin)](https://github.com/synprism-admin)

Every signed release is built by GitHub Actions from this repository's source (see `.github/workflows/release.yml`) and approved for signing by an approver above.

**Privacy:** this program will not transfer any information to other networked systems unless specifically requested by the user or the person installing or operating it. See [PRIVACY.md](PRIVACY.md) for what each feature sends when you use it.

## License

MIT. See [LICENSE](LICENSE). Third-party components and their licenses are listed in [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).
