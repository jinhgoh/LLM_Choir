# LLM Choir

![C#](https://img.shields.io/badge/C%23-WinForms-239120?style=flat-square&logo=csharp&logoColor=white)
![.NET Framework](https://img.shields.io/badge/.NET%20Framework-4.x-512BD4?style=flat-square&logo=dotnet&logoColor=white)
![WebView2](https://img.shields.io/badge/WebView2-1.0.4022.49-0078D7?style=flat-square&logo=microsoftedge&logoColor=white)
![Windows](https://img.shields.io/badge/Windows-10%20%7C%2011-0078D6?style=flat-square&logo=windows&logoColor=white)

<img width="1662" height="992" alt="image" src="https://github.com/user-attachments/assets/c30b2622-db26-4e5a-b2b6-455052c2ee36" />


A single-window "browser-in-a-browser". It shows **ChatGPT, Claude, and
Gemini** side by side, each as a *real* embedded browser (full login, uploads,
voice — every site feature works and sessions persist), with **one shared
prompt box that asks all three at once**.

## Why this design

ChatGPT/Claude/Gemini all send `X-Frame-Options` / `frame-ancestors` headers
that forbid being shown inside another web page, so **Streamlit (or any HTML
page) cannot embed them in iframes** — that's a hard server-side block, not a
tooling limit.

This app uses **Microsoft Edge WebView2** instead. WebView2 is signed by
Microsoft and already built into Windows 11, so — unlike Electron or Playwright's
Firefox — **antivirus (ALYac) does not block it**. No Node, no Electron, no
unsigned Chromium, no AV exclusions.

It's a tiny C# WinForms app compiled with the C# compiler that ships inside
Windows (`csc.exe`), so nothing had to be installed.

## Run

```
run.bat
```

(`run.bat` builds `LLMChoir.exe` on first use, then launches it. To
rebuild after editing the code, run `build.bat`.)

### Use it

> In-app help: click the **?** button at the top-right of the tab bar any time
> for a built-in guide to all the controls and shortcuts.

1. Log in once. All three panels **share one sign-in session**, so signing into
   **Google in any panel signs you into the others** (single sign-on). Sessions
   are saved per-user under `%LOCALAPPDATA%\MultiLLMConsole\profile\` and persist.
   - **Gemini** uses your Google account; WebView2 presents as Edge, which Google
     accepts. Each service still needs a one-time "Sign in with Google" click, but
     you won't re-enter your password/2FA once Google is signed in anywhere.
   - This is the same as being logged into several sites in one normal browser —
     the browser still isolates each site's own cookies by domain; only the
     shared Google session is reused.
2. Type a prompt in the box at the **bottom** of the window and press **Enter**
   to send it to all three (use **Shift+Enter** for a line break), or click
   **Send to all**.
3. Controls (in the bottom bar): two rows of per-service checkboxes —
   **Send:** chooses which LLMs receive the prompt, and **Show:** shows/hides
   each LLM's panel (independent of Send, so you can keep sending to an LLM while
   hiding its window; visible panels expand to fill the freed space). Plus
   **Fill only** (stage text without sending), **Attach to all** (pick file(s)
   once and upload them to every enabled panel at once), **New chat (all)**, and
   per-panel reload (↻).
   - **Attach to all** opens one file picker and drops the chosen file(s) onto
     each site's own uploader, so the attachment appears in all three composers.
     Then type your prompt and **Send to all** as usual. No native dialog pops
     per panel — it uses WebView2's DevTools protocol (`DOM.setFileInputFiles`),
     the same mechanism browser-automation tools use.
4. **Tabs:** the bar at the very top holds comparison tabs. Click **+** to open
   another independent 3-panel comparison, click a tab to switch, and **×** to
   close one. Every tab shares the same login (one profile), but each has its own
   prompt box and its own ChatGPT/Claude/Gemini chats — like separate browser
   tabs. Note: each tab runs three browser panels, so many open tabs use more
   memory, just like a real browser.
   - Shortcuts: **Ctrl+T** new tab, **Ctrl+W** close the current tab,
     **middle-click** a tab to close it. (Closing the last tab opens a fresh one.)
   - A tab is named after the first message you send in it (trimmed with `…`).

## Files

- `MultiLLM.cs` — the whole app (window, three WebView2 panels, broadcast logic).
- `app.manifest` — DPI awareness + Windows 10/11 declaration.
- `Microsoft.Web.WebView2.*.dll` + `WebView2Loader.dll` — WebView2 SDK (from
  NuGet); these must sit **next to the exe** so .NET can load them at runtime.
- `build.bat` / `run.bat` — compile and launch.
- Login/session data is **not** kept in this folder — it lives in one shared
  profile at `%LOCALAPPDATA%\MultiLLMConsole\profile\` (per-user, outside the
  project), so zipping or copying this folder never includes your logins.

## Requirements

- Windows 10/11 with the **Edge WebView2 Runtime** (preinstalled on Win11; on
  older Win10, install the free "Evergreen" runtime from Microsoft).
- The built-in .NET Framework C# compiler (present on all Windows).

## Sharing it (e.g. via GitHub)

This folder is self-contained and safe to share:

1. Anyone on Windows clones/downloads it and runs `run.bat`. It builds the app
   with Windows' built-in C# compiler (no install) and launches.
2. **Logins are not shared.** They live in each user's own
   `%LOCALAPPDATA%\MultiLLMConsole\profile\`, *outside* this folder — so your
   sessions never travel with the code. Each user simply logs into their own
   accounts on first run, and it's remembered on their machine only.

The compiled `.exe` and `crash.log` are git-ignored, and the per-user `profile`
lives outside the project entirely; the WebView2 SDK DLLs are included so it
builds out-of-the-box.

## Tuning

If a site changes its page and the prompt stops landing in the box, update that
site's `InputSelectors` / `SendSelectors` in `MultiLLM.cs` and run `build.bat`.
**Attach to all** uploads in two ways. For sites with a standing hidden
`<input type=file>` (ChatGPT/Claude) it injects the files straight into it via the
DevTools protocol — instant. **Gemini** has no such input (it only creates one
mid-upload, behind a native dialog), so the app intercepts that dialog and drives
Gemini's own uploader: it tries to open it automatically, and if its heuristics
miss, the interception stays armed for ~45 s so **you can click Gemini's own
"＋ ▸ Upload files"** and the files are filled in automatically — no OS dialog
appears either way. The Gemini panel shows a hint (`click + ▸ Upload in Gemini`)
while it waits. If a site stops accepting attachments after a redesign, adjust its
`FileInputSelectors` in `MultiLLM.cs` and rebuild.

## Troubleshooting

- **Self-healing:** on startup the app automatically clears any of its own
  leftover `msedgewebview2` processes (from a previous crash/kill) that would
  otherwise lock the shared profile, and it won't launch a second clashing
  copy — running `run.bat` again just brings the existing window to the front.
  So "double-clicked run.bat and nothing happened" should no longer occur.
- **If a launch ever fails silently:** check `crash.log` in this folder for the
  error.
- **Logins:** all panels share one signed-in session at
  `%LOCALAPPDATA%\MultiLLMConsole\profile\` (outside this project). Delete that
  folder to sign out of everything and start fresh.
