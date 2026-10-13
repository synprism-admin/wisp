# Wisp

A voice assistant that lives at the top of your Windows screen. It types what you say, does things you ask for, answers questions about anything you circle on screen, and connects to your own systems through plugins.

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
- **Apps**: type-to-launch over your Start menu
- **Clipboard**: searchable history; click to paste, right-click to pin or remove
- **Routines**: scheduled and repeating tasks ("every weekday at 9, open Outlook and Teams")
- **Power**: lock, sleep, restart, shut down

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
