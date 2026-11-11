using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using Facetbar.Core;
using Facetbar.Widgets;
using ManagedShell.Common.Helpers;
using NotifyIcon = ManagedShell.WindowsTray.NotifyIcon;

namespace Facetbar.Settings;

public partial class SettingsWindow : Window
{
    private static SettingsWindow? _open;

    private readonly BarSettings _settings = BarSettings.Current.Clone();
    private readonly ObservableCollection<WidgetRow> _widgets = [];
    private readonly List<TrayRow> _tray;

    public static void ShowSingle()
    {
        if (_open != null)
        {
            _open.Activate();
            return;
        }
        _open = new SettingsWindow();
        _open.Closed += (_, _) => _open = null;
        _open.Show();
        _open.Activate();
    }

    private SettingsWindow()
    {
        InitializeComponent();
        DataContext = _settings;

        EdgeBox.ItemsSource = Enum.GetValues<BarEdge>();
        FixedScaleBox.ItemsSource = new[] { 100, 125, 150, 175, 200, 225, 250 };
        SearchModeBox.ItemsSource = new[]
        {
            new ComboBoxItem { Content = "Search with Everything as I type", Tag = SearchMode.Everything },
            new ComboBoxItem { Content = "Send a hotkey (e.g. open Everything / Flow Launcher)", Tag = SearchMode.Hotkey },
            new ComboBoxItem { Content = "Hide the search box", Tag = SearchMode.Hidden },
        };
        StartWithWindowsBox.IsChecked = BarSettings.StartWithWindows;

        // Layout rows: left widgets, right widgets, then everything else (off).
        foreach (var id in _settings.LeftWidgets) AddRow(id, WidgetRow.Left);
        foreach (var id in _settings.RightWidgets) AddRow(id, WidgetRow.Right);
        foreach (var widget in WidgetRegistry.All.Where(w => _widgets.All(r => r.Id != w.Id)))
            AddRow(widget.Id, WidgetRow.Off);
        WidgetList.ItemsSource = _widgets;

        _tray = App.Current.Shell.NotificationArea.TrayIcons
            .Where(i => !string.IsNullOrWhiteSpace(i.Title) || i.Icon != null)
            .Select(i => new TrayRow(i))
            .ToList();
        TrayList.ItemsSource = _tray;
    }

    private void AddRow(string id, string placement)
    {
        if (_widgets.Any(r => r.Id == id)) return;
        var name = WidgetRegistry.Find(id)?.DisplayName ?? $"{id} (not installed)";
        _widgets.Add(new WidgetRow(id, name) { Placement = placement });
    }

    private void MoveUp_Click(object sender, RoutedEventArgs e) => Move(-1);
    private void MoveDown_Click(object sender, RoutedEventArgs e) => Move(1);

    private void Move(int delta)
    {
        if (WidgetList.SelectedItem is not WidgetRow row) return;
        int index = _widgets.IndexOf(row);
        int target = Math.Clamp(index + delta, 0, _widgets.Count - 1);
        if (target == index) return;
        _widgets.Move(index, target);
        WidgetList.SelectedItem = row;
    }

    private void BrowseEverything_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { Filter = "Everything|Everything*.exe|Programs|*.exe", Title = "Find Everything.exe" };
        if (dialog.ShowDialog(this) == true) EverythingPathBox.Text = dialog.FileName;
    }

    private void OpenDataFolder_Click(object sender, RoutedEventArgs e) => ShellHelper.StartProcess(AppPaths.DataDir);

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        _settings.LeftWidgets = _widgets.Where(r => r.Placement == WidgetRow.Left).Select(r => r.Id).ToList();
        _settings.RightWidgets = _widgets.Where(r => r.Placement == WidgetRow.Right).Select(r => r.Id).ToList();

        foreach (var row in _tray.Where(r => r.ShowOnBar != r.Icon.IsPinned))
        {
            if (row.ShowOnBar) row.Icon.Pin();
            else row.Icon.Unpin();
        }
        _settings.PinnedTrayIcons = App.Current.Shell.NotificationArea.PinnedNotifyIcons.ToList();

        try
        {
            BarSettings.StartWithWindows = StartWithWindowsBox.IsChecked == true;
        }
        catch (Exception ex)
        {
            Log.Error("Updating the Run key", ex);
        }

        Close();
        _settings.Save(); // recreates the bars with the new settings
    }

    public sealed class TrayRow(NotifyIcon icon)
    {
        public NotifyIcon Icon { get; } = icon;
        public string Title { get; } = string.IsNullOrWhiteSpace(icon.Title) ? icon.Path : icon.Title;
        public ImageSource? IconImage => Icon.Icon;
        public bool ShowOnBar { get; set; } = icon.IsPinned;
    }
}

public sealed class WidgetRow(string id, string name) : INotifyPropertyChanged
{
    // On a vertical bar the "left" group is at the top and the "right" group at the bottom.
    public const string Left = "Left / top", Right = "Right / bottom", Off = "Off";
    public static string[] Placements { get; } = [Left, Right, Off];

    public string Id { get; } = id;
    public string Name { get; } = name;

    private string _placement = Off;
    public string Placement
    {
        get => _placement;
        set { _placement = value; OnPropertyChanged(); }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public sealed class InverseBoolConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value is not true;
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => value is not true;
}
