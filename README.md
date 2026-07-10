# Hooker 🪝

A little pixel mascot that auto-approves Claude Code prompts and shows, at a
glance, which of your Claude sessions needs you — an always-on-top **widget**
with one mascot tile **per session**.

![waiting+hooking](assets/tile-wait_on.png) ![working+hooking](assets/tile-work_on.png) ![waiting+manual](assets/tile-wait_off.png) ![working+manual](assets/tile-work_off.png)

## Reading a tile (two independent axes)

**Background = does it need you** · **Mascot = is it on autopilot**

| | 🟢 green (your turn) | 🟡 yellow (working) |
|---|---|---|
| **salmon mascot** (hooking) | auto — but waiting on you (e.g. a question) | auto-responding |
| **grey mascot** (manual) | your turn | working, prompts normally |

- **Green background** = that session is **waiting on you** (a prompt, a question, or it finished and wants the next task).
- **Yellow background** = that session is **working** (busy — sit tight).
- **Salmon mascot** = **hooking** (its prompts auto-approve). **Grey mascot** = **manual** (normal prompting).

Each session is independent: one can be on autopilot while another is hand-driven and a third sits waiting.

## The widget

A borderless, always-on-top strip. One tile per live session.

- **Left-click a tile** — toggle that session's hooking (salmon ⇄ grey).
- **Drag a tile** — reorder it (works locked or not), to match your terminal layout.
- **Drag the grip** (dots on the left) — move the whole widget (only when **unlocked**).
- **Hover a tile** — a tooltip (placed cleanly above/below, never over the tiles) shows `name · hooking/manual · working/waiting · N auto-approvals`.
- **Right-click** — menu:
  - **Lock/Unlock position** (pins the widget; tiles still reorder)
  - **Rename…** / **Dismiss** (per tile — custom label, or nuke a stale tile)
  - **New sessions appear** — right (default) or left
  - **Grow direction** — Auto (by screen half), Anchor right (grow left), Anchor left (grow right)
  - **Reset position** (re-dock centered above the taskbar)
  - **Exit**

It stays **in front of the taskbar** (re-asserts top z-order), **hides** while a fullscreen app owns the same monitor (games safe), and defaults to **centered just above the taskbar**. Position, lock, order, anchor, new-session side, labels, and the stale timeout persist to `widget.json`.

### Stale tiles

A session that ends cleanly (`/exit`) removes its tile via `SessionEnd`. An abrupt close (killed terminal, crash) can't, so the widget **auto-prunes** any tile silent for `StaleHours` (default **24h**, `0` disables) — self-healing, since the session's next hook event recreates its tile. Right-click → **Dismiss** clears one instantly.

## How it works

```
widget (HookerWidget.exe) --writes--> ...\.claude\hooker\sessions\<sid>.state   ("on"/"off")
hooks  (hook.exe)         --writes--> ...\.claude\hooker\sessions\<sid>.meta    ({status,cwd,count})
                          <--read---  widget reads all .meta each 250ms to render tiles
```

`hook.exe` is registered on six Claude events, all per session:
- `SessionStart` → new tile (waiting), count reset
- `UserPromptSubmit` / `PreToolUse` → working (`PreToolUse` auto-approves + bumps the count when that session is hooking)
- `AskUserQuestion` (a `PreToolUse`) → waiting (Claude needs you to pick)
- `Stop`, `Notification` → waiting
- `SessionEnd` → removes the session's files

The shim **fails open**: any error → prints nothing, exits 0 → Claude behaves normally. It never blocks Claude.

## Setup

Requires the **.NET 8 runtime** (`dotnet --version`). Python + Pillow only to regenerate icons.

1. **Build** → `dist\hook.exe` and `dist\HookerWidget.exe`:
   ```powershell
   powershell -ExecutionPolicy Bypass -File .\build.ps1
   ```
2. **Register the hooks** — double-click **`Install Hooker.cmd`** (reliable), or run
   `powershell -ExecutionPolicy Bypass -File C:/Playground/Hooker/install-hook.ps1` (forward slashes!).
   > Claude Code's safety guard won't let *Claude* write this hook for you — it's a permission bypass, so you install it deliberately. The exact JSON added is in `dist\settings-hooks-snippet.json`.
3. **Restart** any running Claude Code sessions so they load the hooks.
4. **Run** `dist\HookerWidget.exe` (a tile appears per session).

A login shortcut is at `%APPDATA%\Microsoft\Windows\Start Menu\Programs\Startup\Hooker.lnk`. Remove it with:
```powershell
Remove-Item (Join-Path ([Environment]::GetFolderPath('Startup')) 'Hooker.lnk')
```

## Uninstall

```powershell
powershell -ExecutionPolicy Bypass -File .\uninstall-hook.ps1
```
Removes the hooks. Without them, Claude behaves exactly as stock.

## Troubleshooting

**Nothing auto-approves / hook logs `command not found`.** Claude runs hooks through **bash**, which eats backslashes in a Windows path. The hook command must use **forward slashes**: `C:/Playground/Hooker/dist/hook.exe`. The installer does this; if you hand-edited `settings.json`, fix the slashes and restart Claude.

**Lost the widget?** Right-click any tile → **Reset position**, or it re-docks centered above the taskbar next launch if there's no valid saved spot.

## Caveat

Auto-approve covers every in-session tool/permission/memory prompt, but it can't bypass the initial folder **trust dialog** or other non-tool flows — by design.

## Layout

```
Hooker/
  Mascot.png              source art
  assets/make_icons.py    -> tile-<work|wait>_<on|off>.png  (bg=status, body=hooking)
  shim/                   hook.exe         (.NET console, per-session state)
  tray/                   HookerWidget.exe (.NET WinForms floating widget)
  dist/                   built exes + settings-hooks-snippet.json
  build.ps1  install-hook.ps1  uninstall-hook.ps1  "Install Hooker.cmd"
```
