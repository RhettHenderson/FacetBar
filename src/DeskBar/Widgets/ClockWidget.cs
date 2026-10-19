using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;
using ManagedShell.Common.Helpers;

namespace DeskBar.Widgets;

/// <summary>Time and date like the Windows clock; click for a calendar flyout.</summary>
internal sealed class ClockWidget : IBarWidget
{
    public string Id => "clock";
    public string DisplayName => "Clock and date";

    public FrameworkElement Create(BarContext context) => new ClockButton(context);

    private sealed class ClockButton : Button, IDisposable
    {
        private readonly BarContext _context;
        private readonly TextBlock _time = new() { HorizontalAlignment = HorizontalAlignment.Center };
        private readonly TextBlock _date = new() { HorizontalAlignment = HorizontalAlignment.Center };
        private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
        private readonly Popup _popup;
        private readonly CalendarFlyout _flyout = new();

        public ClockButton(BarContext context)
        {
            _context = context;
            Style = Ui.ButtonStyle;
            Padding = context.IsVertical ? new Thickness(2, 8, 2, 8) : new Thickness(10, 0, 10, 0);
            var panel = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            panel.Children.Add(_time);
            if (context.Settings.ShowDate) panel.Children.Add(_date);
            Content = panel;

            _popup = new Popup
            {
                PlacementTarget = this,
                Placement = context.PopupPlacement,
                HorizontalOffset = 0,
                StaysOpen = false,
                AllowsTransparency = true,
                PopupAnimation = PopupAnimation.Fade,
                Child = _flyout,
            };
            context.TrackPopup(_popup);
            _popup.Opened += (_, _) => _flyout.Reset();
            Click += (_, _) => _popup.IsOpen = !_popup.IsOpen;

            _timer.Tick += (_, _) => Tick();
            _timer.Start();
            Tick();
        }

        private void Tick()
        {
            var now = DateTime.Now;
            var culture = CultureInfo.CurrentCulture;
            var timePattern = _context.Settings.ShowSeconds ? culture.DateTimeFormat.LongTimePattern : culture.DateTimeFormat.ShortTimePattern;
            _time.Text = now.ToString(timePattern, culture);
            _date.Text = now.ToString(culture.DateTimeFormat.ShortDatePattern, culture);
            ToolTip = now.ToString(culture.DateTimeFormat.LongDatePattern, culture);
            if (_popup.IsOpen) _flyout.UpdateTime(now);
        }

        public void Dispose() => _timer.Stop();
    }

    /// <summary>Big clock, long date and a month calendar.</summary>
    private sealed class CalendarFlyout : Border
    {
        private readonly TextBlock _bigTime = new() { FontSize = 40, FontWeight = FontWeights.Light };
        private readonly TextBlock _longDate = new() { Foreground = Ui.Res("AccentBrush"), Margin = new Thickness(0, 0, 0, 12) };
        private readonly TextBlock _monthTitle = new() { FontSize = 14, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
        private readonly UniformGrid _days = new() { Columns = 7, Rows = 7 };
        private DateTime _month;

        public CalendarFlyout()
        {
            Style = (Style)Application.Current.FindResource("PopupBorder");
            Padding = new Thickness(20, 16, 20, 12);
            Width = 340;

            var header = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
            var next = Ui.GlyphButton("", "Next month", 32, () => ShowMonth(_month.AddMonths(1)), 12);
            var prev = Ui.GlyphButton("", "Previous month", 32, () => ShowMonth(_month.AddMonths(-1)), 12);
            next.Height = prev.Height = 32;
            DockPanel.SetDock(next, Dock.Right);
            DockPanel.SetDock(prev, Dock.Right);
            header.Children.Add(next);
            header.Children.Add(prev);
            header.Children.Add(_monthTitle);

            var settingsLink = new Button
            {
                Style = Ui.ButtonStyle,
                HorizontalAlignment = HorizontalAlignment.Left,
                Padding = new Thickness(4),
                Margin = new Thickness(0, 8, 0, 0),
                Content = new TextBlock { Text = "Date and time settings", Foreground = Ui.Res("SubtleForegroundBrush") },
            };
            settingsLink.Click += (_, _) => ShellHelper.StartProcess("ms-settings:dateandtime");

            var stack = new StackPanel();
            stack.Children.Add(_bigTime);
            stack.Children.Add(_longDate);
            stack.Children.Add(new Border { Height = 1, Background = Ui.Res("SeparatorBrush"), Margin = new Thickness(0, 0, 0, 12) });
            stack.Children.Add(header);
            stack.Children.Add(_days);
            stack.Children.Add(settingsLink);
            Child = stack;
        }

        public void Reset()
        {
            UpdateTime(DateTime.Now);
            ShowMonth(new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1));
        }

        public void UpdateTime(DateTime now)
        {
            var culture = CultureInfo.CurrentCulture;
            _bigTime.Text = now.ToString(culture.DateTimeFormat.LongTimePattern, culture);
            _longDate.Text = now.ToString(culture.DateTimeFormat.LongDatePattern, culture);
        }

        private void ShowMonth(DateTime month)
        {
            _month = month;
            var culture = CultureInfo.CurrentCulture;
            _monthTitle.Text = month.ToString("MMMM yyyy", culture);
            _days.Children.Clear();

            var firstDay = culture.DateTimeFormat.FirstDayOfWeek;
            for (int i = 0; i < 7; i++)
            {
                var day = (DayOfWeek)(((int)firstDay + i) % 7);
                _days.Children.Add(Cell(culture.DateTimeFormat.GetShortestDayName(day), Ui.Res("SubtleForegroundBrush"), null));
            }

            int lead = ((int)month.DayOfWeek - (int)firstDay + 7) % 7;
            var start = month.AddDays(-lead);
            for (int i = 0; i < 42; i++)
            {
                var date = start.AddDays(i);
                var brush = date.Month == month.Month ? Ui.Res("ForegroundBrush") : Ui.Res("SubtleForegroundBrush");
                var cell = Cell(date.Day.ToString(culture), brush, date == DateTime.Today ? Ui.Res("AccentBrush") : null);
                if (date.Month != month.Month) cell.Opacity = 0.5;
                _days.Children.Add(cell);
            }
            _days.Rows = 7;
        }

        private static Border Cell(string text, Brush foreground, Brush? background) => new()
        {
            Height = 36,
            Background = background ?? Brushes.Transparent,
            Child = new TextBlock
            {
                Text = text,
                Foreground = foreground,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            },
        };
    }
}
