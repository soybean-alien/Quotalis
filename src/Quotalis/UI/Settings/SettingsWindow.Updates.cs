using System.Globalization;
using Quotalis.Core.Updates;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls.Primitives;

namespace Quotalis.UI.Settings;

/// <summary>
/// Settings, About, Updates: check the private repository's latest release and install it (see <see cref="AppUpdater"/>).
/// The first click checks; when a newer release exists the button becomes "Update to vX.Y.Z", and a confirmation
/// flyout starts the download, verification and installer.
/// </summary>
public sealed partial class SettingsWindow
{
    private bool _updateBusy;

    private void InitUpdates()
    {
        if (AppUpdater.Shared is { } updater)
        {
            Action changed = () => DispatcherQueue.TryEnqueue(RenderUpdates);
            updater.Changed += changed;
            Closed += (_, _) => updater.Changed -= changed;
        }

        RenderUpdates();
    }

    private void RenderUpdates()
    {
        var current = AppUpdater.CurrentVersion.ToString(3);
        if (_isDemo || AppUpdater.Shared is not { } updater)
        {
            UpdateButton.IsEnabled = false;
            UpdateStatusText.Text = _isDemo ? "Not available in demo mode." : "Updates are checked by the running island.";
            return;
        }

        UpdateButton.IsEnabled = !_updateBusy;
        if (_updateBusy)
        {
            return;
        }

        var checkedAt = updater.LastChecked is { } at ? $" Checked {at.ToString("t", CultureInfo.CurrentCulture)}." : string.Empty;
        if (updater.Available is { } release)
        {
            UpdateButton.Content = AppUpdater.IsInstalledCopy ? $"Update to {release.Tag}" : "Check for Updates";
            UpdateStatusText.Text = AppUpdater.IsInstalledCopy
                ? $"{release.Name} is available (you have {current}).{checkedAt}"
                : $"{release.Name} is available (you have {current}). This is a development build, so it does not install updates; update the installed Quotalis.{checkedAt}";
            return;
        }

        UpdateButton.Content = "Check for Updates";
        UpdateStatusText.Text = updater.LastError is { } error
            ? $"{error}{checkedAt}"
            : updater.LastChecked is null
                ? $"Version {current}. Updates come from the {UpdateService.Repo} releases on GitHub."
                : $"You have the latest version, {current}.{checkedAt}";
    }

    private async void OnUpdateClick(object sender, RoutedEventArgs e)
    {
        if (_updateBusy || AppUpdater.Shared is not { } updater)
        {
            return;
        }

        if (updater.Available is { } release && AppUpdater.IsInstalledCopy)
        {
            UpdateConfirmTitle.Text = $"Update to {release.Name}?";
            FlyoutBase.ShowAttachedFlyout(UpdateButton);
            return;
        }

        SetUpdateBusy("Checking GitHub for the latest release…");
        try
        {
            await updater.CheckAsync();
        }
        catch (UpdateException)
        {
            // LastError carries the message.
        }
        finally
        {
            _updateBusy = false;
            RenderUpdates();
        }
    }

    private async void OnConfirmUpdate(object sender, RoutedEventArgs e)
    {
        UpdateFlyout.Hide();
        if (_updateBusy || AppUpdater.Shared is not { Available: { } release } updater)
        {
            return;
        }

        SetUpdateBusy($"Downloading and verifying {release.Installer}…");
        try
        {
            await updater.StartInstallAsync(release);
            UpdateStatusText.Text = "Installing. Quotalis closes now and starts again when the update is done.";
            QuitRequested?.Invoke();
        }
        catch (Exception ex) when (ex is UpdateException or IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            _updateBusy = false;
            RenderUpdates();
            UpdateStatusText.Text = ex is UpdateException ? ex.Message : $"The update could not start: {ex.Message}";
        }
    }

    private void SetUpdateBusy(string status)
    {
        _updateBusy = true;
        UpdateButton.IsEnabled = false;
        UpdateStatusText.Text = status;
    }
}
