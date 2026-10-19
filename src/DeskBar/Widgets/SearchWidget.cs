using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using DeskBar.Core;
using ManagedShell.Common.Enums;
using ManagedShell.Common.Helpers;

namespace DeskBar.Widgets;

/// <summary>
/// Search box. In Everything mode results appear in a flyout as you type; in Hotkey mode clicking it
/// sends a key combination (e.g. Alt+Space) to open your launcher instead.
/// </summary>
internal sealed class SearchWidget : IBarWidget
{
    public string Id => "search";
    public string DisplayName => "Search box (Everything)";

    public FrameworkElement Create(BarContext context) => context.Settings.SearchMode switch
    {
        SearchMode.Hidden => new Border { Width = 0 },
        SearchMode.Hotkey => CreateHotkeyBox(context),
        _ => new EverythingSearchBox(context),
    };

    private static FrameworkElement CreateHotkeyBox(BarContext context)
    {
        var hotkey = context.Settings.SearchHotkey;
        var box = SearchBoxChrome(context, new TextBlock
        {
            Text = $"Search ({hotkey})",
            Foreground = Ui.Res("SubtleForegroundBrush"),
            VerticalAlignment = VerticalAlignment.Center,
        });
        box.Cursor = Cursors.Hand;
        box.MouseLeftButtonUp += (_, _) => Hotkeys.Send(hotkey);
        return box;
    }

    internal static Border SearchBoxChrome(BarContext context, UIElement content)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        var glyph = Ui.Glyph("", 14);
        glyph.Margin = new Thickness(10, 0, 8, 0);
        grid.Children.Add(glyph);
        Grid.SetColumn(content, 1);
        grid.Children.Add(content);
        return new Border
        {
            Width = Math.Clamp(context.Settings.SearchBoxWidth, 120, 800),
            Margin = new Thickness(4, 5, 4, 5),
            Background = Ui.Res("InputBackground"),
            Child = grid,
        };
    }

    private sealed class EverythingSearchBox : ContentControl, IDisposable
    {
        private readonly BarContext _context;
        private readonly EverythingClient _client = new();
        private readonly TextBox _input;
        private readonly TextBlock _placeholder;
        private readonly Border _chrome;
        private readonly TextBlock _glyph;
        private readonly Popup _popup;
        private readonly StackPanel _rows = new();
        private readonly TextBlock _status = new() { Margin = new Thickness(12, 8, 12, 8), Foreground = Ui.Res("SubtleForegroundBrush"), TextWrapping = TextWrapping.Wrap };
        private readonly DispatcherTimer _debounce = new() { Interval = TimeSpan.FromMilliseconds(90) };
        private List<EverythingResult> _results = [];
        private int _selected;

        public EverythingSearchBox(BarContext context)
        {
            _context = context;
            _input = new TextBox
            {
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Foreground = Ui.Res("ForegroundBrush"),
                CaretBrush = Ui.Res("ForegroundBrush"),
                VerticalContentAlignment = VerticalAlignment.Center,
                FontSize = 13,
                Padding = new Thickness(0),
            };
            _placeholder = new TextBlock
            {
                Text = "Type here to search",
                Foreground = Ui.Res("SubtleForegroundBrush"),
                VerticalAlignment = VerticalAlignment.Center,
                IsHitTestVisible = false,
                Margin = new Thickness(2, 0, 0, 0),
            };
            var inputHost = new Grid();
            inputHost.Children.Add(_placeholder);
            inputHost.Children.Add(_input);
            _chrome = SearchBoxChrome(context, inputHost);
            _glyph = (TextBlock)((Grid)_chrome.Child).Children[0];
            Content = _chrome;

            var footer = new Button
            {
                Style = Ui.ButtonStyle,
                HorizontalContentAlignment = HorizontalAlignment.Left,
                Padding = new Thickness(12, 8, 12, 8),
                Content = new TextBlock { Text = "Show all results in Everything   (Shift+Enter)", Foreground = Ui.Res("SubtleForegroundBrush") },
            };
            footer.Click += (_, _) => OpenInEverything();

            var panel = new StackPanel();
            panel.Children.Add(_status);
            panel.Children.Add(_rows);
            panel.Children.Add(new Border { Height = 1, Background = Ui.Res("SeparatorBrush") });
            panel.Children.Add(footer);

            _popup = new Popup
            {
                PlacementTarget = _chrome,
                Placement = context.PopupPlacement,
                StaysOpen = true, // closed explicitly when the box loses focus
                AllowsTransparency = true,
                Child = new Border
                {
                    Style = (Style)Application.Current.FindResource("PopupBorder"),
                    Width = Math.Max(_chrome.Width, 560),
                    Child = panel,
                },
            };
            context.TrackPopup(_popup);

            _chrome.PreviewMouseLeftButtonDown += (_, _) =>
            {
                context.Bar.ActivateForInput();
                _input.Focus();
            };
            _input.GotKeyboardFocus += (_, _) => SetFocusedLook(true);
            _input.LostKeyboardFocus += (_, e) =>
            {
                if (e.NewFocus is DependencyObject d && IsInPopup(d)) return;
                SetFocusedLook(false);
                _popup.IsOpen = false;
            };
            _input.TextChanged += (_, _) =>
            {
                _placeholder.Visibility = _input.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
                _debounce.Stop();
                _debounce.Start();
            };
            _input.PreviewKeyDown += OnKeyDown;
            _debounce.Tick += async (_, _) =>
            {
                _debounce.Stop();
                await RunQueryAsync();
            };
        }

        private bool IsInPopup(DependencyObject d)
        {
            for (var current = d; current != null; current = VisualTreeHelper.GetParent(current) ?? LogicalTreeHelper.GetParent(current))
                if (current == _popup.Child) return true;
            return false;
        }

        private void SetFocusedLook(bool focused)
        {
            // Windows 10 style: the box turns white while you type in it.
            _chrome.Background = focused ? Ui.Res("InputFocusedBackground") : Ui.Res("InputBackground");
            var text = focused ? Brushes.Black : Ui.Res("ForegroundBrush");
            _input.Foreground = text;
            _input.CaretBrush = text;
            _glyph.Foreground = text;
            _placeholder.Foreground = focused ? new SolidColorBrush(Color.FromRgb(0x60, 0x60, 0x60)) : Ui.Res("SubtleForegroundBrush");
        }

        private async Task RunQueryAsync()
        {
            var query = _input.Text.Trim();
            if (query.Length == 0)
            {
                _popup.IsOpen = false;
                return;
            }

            if (!EverythingClient.IsRunning)
            {
                ShowStatus("Everything isn't running. Press Enter to start it.");
                _results = [];
                RenderRows();
                return;
            }

            var raw = await _client.QueryAsync(query, 300);
            if (query != _input.Text.Trim()) return; // a newer query is on its way
            if (raw == null)
            {
                ShowStatus("Everything didn't answer.");
                return;
            }

            _results = Rank(raw, query).Take(Math.Clamp(_context.Settings.SearchMaxResults, 3, 30)).ToList();
            _selected = 0;
            ShowStatus(_results.Count == 0 ? "No results." : null);
            RenderRows();
        }

        private void ShowStatus(string? text)
        {
            _status.Text = text ?? "";
            _status.Visibility = text == null ? Visibility.Collapsed : Visibility.Visible;
            _popup.IsOpen = true;
        }

        /// <summary>Apps and exact name matches first; system noise (WinSxS etc.) last.</summary>
        private static IEnumerable<EverythingResult> Rank(IReadOnlyList<EverythingResult> results, string query)
        {
            static bool IsApp(EverythingResult r) =>
                !r.IsFolder && (r.Name.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase) || r.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                                || r.Name.EndsWith(".appref-ms", StringComparison.OrdinalIgnoreCase));

            static bool IsNoise(EverythingResult r) =>
                r.Directory.Contains(@"\WinSxS", StringComparison.OrdinalIgnoreCase)
                || r.Directory.Contains(@"\servicing\", StringComparison.OrdinalIgnoreCase)
                || r.Directory.Contains("$Recycle.Bin", StringComparison.OrdinalIgnoreCase)
                || r.Directory.Contains(@"\Prefetch", StringComparison.OrdinalIgnoreCase)
                || r.Directory.Contains(@"\AppData\Local\Temp", StringComparison.OrdinalIgnoreCase)
                || r.Directory.Contains(@"\Installer\", StringComparison.OrdinalIgnoreCase);

            int MatchRank(EverythingResult r)
            {
                var stem = System.IO.Path.GetFileNameWithoutExtension(r.Name);
                if (stem.Equals(query, StringComparison.OrdinalIgnoreCase)) return 0;
                if (r.Name.StartsWith(query, StringComparison.OrdinalIgnoreCase)) return 1;
                if (stem.Split(' ', '-', '_', '.').Any(w => w.StartsWith(query, StringComparison.OrdinalIgnoreCase))) return 2;
                return 3;
            }

            bool InStartMenu(EverythingResult r) => r.Directory.Contains(@"\Start Menu\", StringComparison.OrdinalIgnoreCase);

            return results
                .OrderBy(IsNoise)
                .ThenBy(MatchRank)
                .ThenByDescending(r => IsApp(r) && InStartMenu(r))
                .ThenByDescending(IsApp)
                .ThenBy(r => r.Directory.Length);
        }

        private void RenderRows()
        {
            _rows.Children.Clear();
            for (int i = 0; i < _results.Count; i++)
            {
                int index = i;
                var r = _results[i];
                var row = new Border
                {
                    Padding = new Thickness(12, 6, 12, 6),
                    Background = i == _selected ? Ui.Res("AccentSoftBrush") : Brushes.Transparent,
                    Cursor = Cursors.Hand,
                };
                var grid = new Grid();
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(32) });
                grid.ColumnDefinitions.Add(new ColumnDefinition());
                grid.Children.Add(new Image { Source = IconFor(r), Width = 20, Height = 20, HorizontalAlignment = HorizontalAlignment.Left });
                var text = new StackPanel();
                text.Children.Add(new TextBlock { Text = DisplayName(r), FontSize = 13, TextTrimming = TextTrimming.CharacterEllipsis });
                text.Children.Add(new TextBlock { Text = r.Directory, Foreground = Ui.Res("SubtleForegroundBrush"), FontSize = 11, TextTrimming = TextTrimming.CharacterEllipsis });
                Grid.SetColumn(text, 1);
                grid.Children.Add(text);
                row.Child = grid;
                row.MouseEnter += (_, _) => Select(index);
                row.MouseLeftButtonUp += (_, _) => Open(r, Keyboard.Modifiers);
                row.MouseRightButtonUp += (_, e) =>
                {
                    e.Handled = true;
                    ShowResultMenu(r);
                };
                _rows.Children.Add(row);
            }
        }

        private static string DisplayName(EverythingResult r) =>
            r.Name.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase) ? r.Name[..^4] : r.Name;

        private static ImageSource IconFor(EverythingResult r)
        {
            try { return IconImageConverter.GetImageFromAssociatedIcon(r.FullPath, IconSize.Small); }
            catch { return IconImageConverter.GetDefaultIcon(); }
        }

        private void Select(int index)
        {
            if (_results.Count == 0) return;
            _selected = (index + _results.Count) % _results.Count;
            for (int i = 0; i < _rows.Children.Count; i++)
                ((Border)_rows.Children[i]).Background = i == _selected ? Ui.Res("AccentSoftBrush") : Brushes.Transparent;
        }

        private void OnKeyDown(object sender, KeyEventArgs e)
        {
            switch (e.Key)
            {
                case Key.Down: Select(_selected + 1); e.Handled = true; break;
                case Key.Up: Select(_selected - 1); e.Handled = true; break;
                case Key.Escape:
                    if (_input.Text.Length > 0) _input.Clear();
                    else Keyboard.ClearFocus();
                    _popup.IsOpen = false;
                    e.Handled = true;
                    break;
                case Key.Enter:
                    e.Handled = true;
                    if (!EverythingClient.IsRunning) { EverythingClient.StartEverything(); break; }
                    if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) { OpenInEverything(); break; }
                    if (_results.Count > 0) Open(_results[_selected], Keyboard.Modifiers);
                    break;
            }
        }

        /// <summary>Enter opens; Ctrl+Enter / Ctrl+click shows it in its folder.</summary>
        private void Open(EverythingResult r, ModifierKeys modifiers)
        {
            try
            {
                if (modifiers.HasFlag(ModifierKeys.Control))
                    Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{r.FullPath}\"") { UseShellExecute = true });
                else
                    Process.Start(new ProcessStartInfo(r.FullPath) { UseShellExecute = true, WorkingDirectory = r.Directory });
            }
            catch (Exception ex)
            {
                Log.Error($"Opening {r.FullPath}", ex);
            }
            Finish();
        }

        private void ShowResultMenu(EverythingResult r)
        {
            var menu = new ContextMenu();
            _context.TrackMenu(menu);
            menu.Items.Add(Bar.BarWindow.MenuItem("Open", () => Open(r, ModifierKeys.None)));
            menu.Items.Add(Bar.BarWindow.MenuItem("Open file location", () => Open(r, ModifierKeys.Control)));
            menu.Items.Add(Bar.BarWindow.MenuItem("Run as administrator", () =>
            {
                try { Process.Start(new ProcessStartInfo(r.FullPath) { UseShellExecute = true, Verb = "runas" }); }
                catch (System.ComponentModel.Win32Exception) { } // UAC cancelled
                Finish();
            }));
            menu.Items.Add(new Separator());
            menu.Items.Add(Bar.BarWindow.MenuItem("Pin to this desktop", () => Pin(r, PinScope.ThisDesktop)));
            menu.Items.Add(Bar.BarWindow.MenuItem("Pin to all desktops", () => Pin(r, PinScope.AllDesktops)));
            menu.Items.Add(new Separator());
            menu.Items.Add(Bar.BarWindow.MenuItem("Copy path", () => Clipboard.SetText(r.FullPath)));
            menu.Items.Add(Bar.BarWindow.MenuItem("Properties", () => ShellHelper.ShowFileProperties(r.FullPath)));
            menu.IsOpen = true;
        }

        private void Pin(EverythingResult r, PinScope scope)
        {
            _context.Pins.Pin(PinnedApp.FromPath(r.FullPath), scope, _context.Desktops.Current.Id);
            Finish();
        }

        private void OpenInEverything()
        {
            EverythingClient.OpenInEverything(_input.Text.Trim());
            Finish();
        }

        private void Finish()
        {
            _popup.IsOpen = false;
            _input.Clear();
            Keyboard.ClearFocus();
        }

        public void Dispose()
        {
            _debounce.Stop();
            _popup.IsOpen = false;
            _client.Dispose();
        }
    }
}

/// <summary>Sends key combinations written like "Alt+Space" or "Ctrl+Shift+F".</summary>
internal static class Hotkeys
{
    public static void Send(string combo)
    {
        var parts = combo.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        var keys = new List<byte>();
        foreach (var part in parts)
        {
            byte? vk = part.ToLowerInvariant() switch
            {
                "ctrl" or "control" => 0x11,
                "alt" => 0x12,
                "shift" => 0x10,
                "win" or "windows" => 0x5B,
                _ => Enum.TryParse<Key>(part, ignoreCase: true, out var key) ? (byte)KeyInterop.VirtualKeyFromKey(key) : null,
            };
            if (vk == null)
            {
                Log.Write($"Unknown key '{part}' in hotkey '{combo}'");
                return;
            }
            keys.Add(vk.Value);
        }
        foreach (var k in keys) Native.keybd_event(k, 0, 0, UIntPtr.Zero);
        for (int i = keys.Count - 1; i >= 0; i--) Native.keybd_event(keys[i], 0, Native.KEYEVENTF_KEYUP, UIntPtr.Zero);
    }
}
