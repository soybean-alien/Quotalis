using System.Globalization;
using Microsoft.UI.Xaml.Controls;

namespace Claudex.UI.Settings;

/// <summary>Settings, General, Claude peak hours: the start and end hour (US Eastern, weekdays).</summary>
public sealed partial class SettingsWindow
{
    private static string HourLabel(int hour) =>
        hour == 24 ? "Midnight" : new DateTime(2000, 1, 1, hour, 0, 0).ToString("h tt", CultureInfo.CurrentCulture);

    private void InitPeakHours()
    {
        for (var h = 0; h < 24; h++)
        {
            PeakStartPicker.Items.Add(new ComboBoxItem { Content = HourLabel(h), Tag = h });
        }

        for (var h = 1; h <= 24; h++)
        {
            PeakEndPicker.Items.Add(new ComboBoxItem { Content = HourLabel(h), Tag = h });
        }
    }

    private void LoadPeakHours(Core.Settings.AppSettings s)
    {
        PeakStartPicker.SelectedIndex = Math.Clamp(s.PeakStartHour, 0, 23);
        PeakEndPicker.SelectedIndex = Math.Clamp(s.PeakEndHour, 1, 24) - 1;
    }

    private void OnPeakHoursChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || PeakStartPicker.SelectedItem is not ComboBoxItem { Tag: int start } || PeakEndPicker.SelectedItem is not ComboBoxItem { Tag: int end })
        {
            return;
        }

        if (end <= start)
        {
            // An empty or reversed window is not a peak window: keep the end one hour after the start.
            end = start + 1;
            _loading = true;
            PeakEndPicker.SelectedIndex = end - 1;
            _loading = false;
        }

        _store.Update(s =>
        {
            s.PeakStartHour = start;
            s.PeakEndHour = end;
        });
    }
}
