# Facetbar

Facetbar is a modular replacement for the Windows 10 taskbar. Its main feature is that **each virtual desktop has its own pinned apps**, and there's also a shared set of pins that shows on every desktop.

It's built on [ManagedShell](https://github.com/cairoshell/ManagedShell) (Apache-2.0), the same library the RetroBar taskbar uses.

## Features

| Part | What it does |
|---|---|
| **Start** | Opens the Start menu. Right-click opens the Win+X menu. |
| **Search** | Shows live [Everything](https://www.voidtools.com/) results as you type (more below). |
| **Task View + desktop name** | Shows which virtual desktop you're on. Click it for Task View. |
| **Pinned and running apps** | Pinned apps for the current desktop, merged with the windows that are open on it. Hover for live window thumbnails; hover a thumbnail to peek at that window. |
| **Notification area** | Tray icons, with a ^ flyout for the ones you choose to hide. Drag icons between the bar and the flyout. |
| **Clock** | Time and date. Click for a calendar. |
| **Action Center** | Opens Windows' Action Center. |
| **Show desktop** | The thin strip at the far right minimizes or restores all windows. |

The bar also:
- **Position:** goes on any screen edge: bottom, top, left or right. On the left or right, the search box becomes a magnifier button, tray icons wrap into rows, and the side width is set in Settings.
- **Scaling:** follows each monitor's Windows display scale. At 100% size it's as tall as the Windows 10 taskbar.
- **Start menu:** pressing the Win key shows an auto-hidden bar, and Facetbar nudges the Start menu so it doesn't cover the bar. The Start menu draws above every normal window, so the bar can't sit on top of it. Both behaviors can be turned off in Settings.
- **Auto-hide:** can slide away and reappear when the mouse comes within a set distance of the edge (12 px by default, adjustable in Settings). It also reappears over maximized windows, but stays out of the way of real full-screen content like videos and games.
- **Monitors:** runs on every monitor, and every bar shows all your apps. Settings can limit each bar to the windows on its own monitor.
- **Windows taskbar:** hides the Windows taskbar while it runs, and restores it when it exits, even after a crash.

## Running

Build it:

```bash
dotnet publish src/Facetbar/Facetbar.csproj -c Release -o publish/Facetbar
```

Then run `publish\Facetbar\Facetbar.exe`. It needs the .NET 8 Desktop Runtime.

| Command | What it does |
|---|---|
| `Facetbar.exe` | Start the bar |
| `Facetbar.exe --exit` | Close a running Facetbar |
| `Facetbar.exe --restore-taskbar` | Bring back the Windows taskbar if it's ever stuck hidden |
| `Facetbar.exe --selftest` | Check desktops, pins, shortcuts and Everything without opening a bar; writes `selftest.txt` |

To configure it, right-click an empty part of the bar and choose **Facetbar settings…**. Settings include position, size, scaling, auto-hide, monitors, the clock, the search box, the layout and the tray icons. **Start with Windows** is also there.

### Coming from DeskBar or Linkbar Desktops

Facetbar was called **DeskBar** until version 3.0, and before that it was **Linkbar Desktops**, which ran one [Linkbar](https://github.com/ATGH15102AFMLD/Linkbar) per desktop. When Facetbar starts, it asks either older app to quit if it's running, and on first run it carries your setup over:

- **From DeskBar:** your settings and pins are copied from `%APPDATA%\DeskBar` to `%APPDATA%\Facetbar`, and the **Start with Windows** entry is moved to Facetbar. The old folder is left in place, so you can delete it once you're happy.
- **From Linkbar Desktops:** the shortcuts you had pinned are imported.

## Pinned apps

| Action | How |
|---|---|
| Pin an app | Right-click a running app and choose **Pin to this desktop** or **Pin to all desktops**. You can also drag files or shortcuts onto the bar, or right-click a search result. |
| Move a pin between "this desktop" and "all desktops" | Right-click it and toggle **Show on all desktops**. |
| Reorder pins | Drag them. |
| Start a new window | Middle-click or Shift+click the app. |
| Switch to or minimize a window | Click the app. If it has several windows, click to choose one from the thumbnails. |

Pins are stored in `%APPDATA%\Facetbar\pins.json`.

Some apps' windows belong to a helper program. Steam's windows, for example, come from `steamwebhelper.exe`, which `steam.exe` starts. Pinning such a window pins the program that launched the helper, and older pins that point at a helper are repaired automatically the next time the app is running. Windows that don't report an icon (common with games) show their program's icon instead.

Store apps such as the new Outlook or Claude live in versioned folders under `C:\Program Files\WindowsApps` that change on every update. Facetbar pins and launches them by their app ID instead of by path. Older pins that point into `WindowsApps` are converted automatically when Facetbar loads them.

## Search (Everything)

The search box talks to your running Everything instance directly through its IPC interface, so no SDK or extra download is needed. Results put apps and exact name matches first, and push system clutter like WinSxS to the bottom.

| Keys | What it does |
|---|---|
| ↑ / ↓ | Choose a result |
| Enter | Open it |
| Ctrl+Enter | Show it in its folder |
| Shift+Enter | Open the full results in Everything |
| Esc | Clear the box, or close it if it's already empty |
| Right-click a result | Run as administrator, pin, copy path, properties |

If you'd rather have the box open your launcher (for example Everything's window or Flow Launcher) with a hotkey such as Alt+Space, change **Search box → When I use it** in Settings.

## Files

```
%APPDATA%\Facetbar\
  settings.json        all settings (safe to hand-edit; missing values use defaults)
  pins.json            pinned apps: "AllDesktops" plus one list per desktop GUID
  log.txt              diagnostics
  Plugins\             extra widget DLLs (see below)
  ImportedLinks\       shortcuts carried over from the Linkbar version
```

## Extending it

Every part of the bar is a widget that implements `Facetbar.Widgets.IBarWidget`:

```csharp
public sealed class CpuWidget : IBarWidget
{
    public string Id => "cpu";
    public string DisplayName => "CPU usage";
    public FrameworkElement Create(BarContext context) => new TextBlock { Text = "CPU 3%" };
}
```

There are two ways to add one:
- **Inside Facetbar:** register it in `WidgetRegistry.Initialize()`.
- **As a plugin:** build it in a separate class library that references `Facetbar.dll`, and drop the DLL into `%APPDATA%\Facetbar\Plugins`.

Either way, the widget then shows up in **Settings → Layout**.

`BarContext` gives widgets access to:
- ManagedShell's services (tasks, tray, appbars)
- the virtual desktop service
- the pin store
- the settings
- popup placement and auto-hide helpers

Widgets that implement `IDisposable` are disposed when the bar is rebuilt.

### Source layout

```
src/Facetbar/
  App.xaml(.cs)        startup, single instance, command-line switches, crash safety
  Bar/                 BarWindow (ManagedShell AppBarWindow), BarManager (one bar per monitor)
  Core/                settings, virtual desktops, pins, .lnk reading, taskbar guard, theme, self-test,
                       Start menu monitor, helper-process tracing, DeskBar migration
  Widgets/             start, search + Everything client, tray, clock, simple widgets
  Widgets/Tasks/       task list, task buttons, window previews (DWM thumbnails)
  Settings/            settings window
  Themes/Dark.xaml     colours, button style, dark menus and tooltips
```

## Limitations and ideas

- **Win+1…9:** these shortcuts still go to the hidden Windows taskbar, because Explorer owns them.
- **Progress bars and overlay badges on task buttons:** these only appear when Windows routes them to Facetbar. Explorer usually keeps them.
- **Ideas for later:** jump lists, a volume/media widget, a system monitor, and per-desktop accent colours or wallpapers.

## Version history

Each release is a tagged commit (`git tag -l`). Full notes are in each commit message.

| Version | Highlights |
|---|---|
| 3.0 | Renamed from DeskBar to Facetbar. Your DeskBar settings, pins and startup entry carry over. |
| 2.8 | The bar hides as quickly as the Start menu does; adjustable hide delays. |
| 2.7 | The Start menu is found and slid clear of the bar. |
| 2.6 | Fixed a crash the first time the Start menu opened. |
| 2.5 | Win key shows the bar; drag tray icons between the bar and the flyout; Steam-style helper processes; game icons. |
| 2.4 | Running apps whose exe was renamed (e.g. `Zing.prev.exe`) match their pins. |
| 2.3 | Running apps stand out more; auto-hide no longer gets stuck. |
| 2.2 | Store-app icons, every app on every monitor, reveal distance for auto-hide, maximized vs. full-screen. |
| 2.1 | Left and right edges; crash fix when switching desktops; Store apps pinned by app ID. |
| 2.0 | DeskBar: a modular WPF taskbar replacing Linkbar. |
| 1.0 | Linkbar Desktops: one Linkbar per virtual desktop. |
