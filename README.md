# Linkbar Desktops

Gives each Windows 10 virtual desktop its own [Linkbar](https://github.com/ATGH15102AFMLD/Linkbar) with its own pinned apps.

A small tray app runs one Linkbar instance per virtual desktop. When you switch desktops, it shows that desktop's bar and hides the others. All bars sit in the same spot, and only the visible bar reserves screen space, so maximized windows never get pushed down by hidden bars.

## Requirements

- Windows 10 (Windows 11 should also work; it stores the current desktop in a slightly different registry key, which the app checks)
- .NET 8 Desktop Runtime
- Linkbar 1.6.9 (included in `Linkbar\`)

## Running it

Build:

```bash
dotnet publish src/LinkbarDesktops.csproj -c Release -o dist
```

Then run `dist\LinkbarDesktops.exe`. A Linkbar icon appears in the system tray.

- **Pin apps:** drag shortcuts onto the bar, or use the bar's right-click menu, the same as normal Linkbar. Each desktop's links live in its own folder.
- **New desktops:** the first time you visit a desktop, it gets an empty bar automatically.
- **Make all bars look the same:** set up one bar's appearance in Linkbar's *Properties* (edge, icon size, colors, etc.), then choose *Copy this bar's style to all desktops* from the tray menu. Only the look and position are copied, not the links. Bars for desktops you haven't visited yet will use that style too.

### Tray menu

| Item | What it does |
|---|---|
| Open links folder for… | Opens a desktop's shortcut folder in Explorer. Left-clicking the tray icon opens the current desktop's folder. |
| Copy this bar's style to all desktops | Copies the current bar's settings, except its links folder, to every other bar and restarts them. |
| Restart all bars | Closes and relaunches every bar. |
| Open data folder | Opens `%APPDATA%\LinkbarDesktops`. |
| Start with Windows | Adds or removes a `HKCU\...\Run` entry. |
| Exit | Closes all bars and quits. |

`LinkbarDesktops.exe --exit` closes a running instance from a script.

## Where things are stored

```
%APPDATA%\LinkbarDesktops\
  config.json          LinkbarPath: leave empty to use the Linkbar folder next to the exe
  template.lbr         style used for new desktops (written by "Copy this bar's style…")
  log.txt              diagnostics
  Desktops\<desktop GUID>\
    bar.lbr            this desktop's Linkbar settings
    links\             this desktop's shortcuts
    desktop-name.txt   which desktop this folder belongs to
```

Folders are named by the desktop's GUID, so renaming a desktop keeps its bar. If you delete a desktop in Task View, its bar is closed but its folder is kept.

These bars are separate from any bars you create with plain Linkbar (which live in `%APPDATA%\Linkbar\User bars`). Linkbar's own autostart won't launch them.

## How it works

- **Current desktop:** read from the registry values Explorer maintains (`HKCU\...\Explorer\SessionInfo\<session>\VirtualDesktops\CurrentVirtualDesktop`, plus `...\Explorer\VirtualDesktops` for the desktop list and names). The app gets change notifications through `RegNotifyChangeKeyValue`, with a 1-second poll as a fallback. It doesn't use the undocumented virtual desktop COM interfaces, which break between Windows builds.
- **Show/hide:** Linkbar windows are tool windows, so Windows shows them on every desktop. To hide a bar, the app hides its window and removes its appbar registration (`ABM_REMOVE`), which frees the screen space. To show a bar, it re-registers the window with Linkbar's callback message (`WM_USER+1`) and sends Linkbar an `ABN_POSCHANGED`, so Linkbar re-runs its own positioning code.
- **Explorer restarts:** Linkbar re-registers every bar when Explorer restarts. The app listens for `TaskbarCreated` and hides the non-current bars again.
- **Resource use:** each idle Linkbar instance uses about 25–30 MB of RAM and essentially no CPU.

## Limitations

- **Autohide:** Windows allows only one autohide appbar per screen edge, so enabling Linkbar's autohide on several bars on the same edge won't work. Autohide bars are shown and hidden but their appbar state is left alone.
- **Closing a bar from its own menu:** a bar closed with Linkbar's *Close* item stays closed until you leave that desktop and come back. *Remove linkbar* deletes its settings, and you get a fresh empty bar on your next visit.
