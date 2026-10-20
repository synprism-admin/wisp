# Wisp privacy policy

Wisp runs on your PC. It has no servers of its own, no accounts, and no analytics or telemetry. This page lists everything Wisp stores and every case where data leaves your computer.

## What stays on your PC

- **Your voice.** Speech is turned into text locally, on your own GPU or CPU, using the Whisper model. Audio is never uploaded and never saved.
- **Settings**, in `%APPDATA%\Wisp\settings.json`. API keys are encrypted with Windows (DPAPI) so only your Windows account can read them.
- **To-dos, routines and clipboard history**, in `%APPDATA%\Wisp\wisp.db` on your PC. Clipboard history keeps the text you copy (the most recent 500 items) so you can paste it again from the Clipboard tab. You can pin or remove items, or delete the file.
- **Logs**, in `%APPDATA%\Wisp\logs`. They record what Wisp did, including which tools the assistant ran and with what inputs, to help diagnose problems.

## When data leaves your PC

Only when you use an AI feature, and only to the AI provider you chose, using **your own API key**:

| Provider | Address |
|---|---|
| Anthropic (Claude) | `api.anthropic.com` |
| Google (Gemini) | `generativelanguage.googleapis.com` |

What gets sent, by feature:

- **Talking to Wisp / the Ask box:** what you said or typed, your Windows user name, and the current date and time. If the assistant uses tools to answer, their results are sent too. Depending on the request that can include the titles of open windows, the names and contents of controls in a window it's asked to work with, a **screenshot** of a window or screen, clipboard text, file and folder names, command output, or replies from your plugins.
- **Dictation clean-up:** the transcript, plus the name and title of the app you're typing into (so the wording fits the app). You can turn clean-up off in Settings; the raw transcript is then typed without being sent anywhere.
- **Circle to ask:** a screenshot of the area you circled (with the rest of the screen dimmed), and your spoken question.
- **Scheduled routines:** the routine's instruction when it runs, plus any tool results as above.

How each provider handles that data is governed by their terms: [Anthropic](https://www.anthropic.com/legal/privacy), [Google](https://ai.google.dev/gemini-api/terms).

**Turning it off:** Settings → **AI features** off stops Wisp sending anything to an AI provider. Dictation still works (raw transcripts, no clean-up); voice commands, the Ask box, circle-to-ask and routines are disabled. With no API key set, nothing is sent either.

## Other network use

- **Speech model download:** on first run Wisp downloads the Whisper speech model (about 550 MB) from Hugging Face (`huggingface.co`). Nothing about you is sent beyond a normal download request.
- **Plugins** you add send requests to the addresses written in their files, and nowhere else. Wisp ships one example plugin, switched off.
- **Links and searches** you ask for open in your web browser.

## Changes to Windows

When you turn on **Wisp desktop**, Wisp hides the Windows taskbar, registers its own top bar and dock, and can change Windows' dark mode and accent colors. It asks before doing this. Your original settings are saved, and they're restored when you turn Wisp desktop off, when Wisp exits, and when you uninstall it.

## Contact

Questions or concerns: open an issue on the project's GitHub page.
