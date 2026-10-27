using Quotalis.Core.Diagnostics;
using Quotalis.Core.Formatting;
using Quotalis.Core.Settings;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage.Pickers;
using Windows.System;

namespace Quotalis.UI.Settings;

/// <summary>Settings, Modules section: the Nous Portal sign-in, where Hermes's token use comes from, the low-balance mark.</summary>
public sealed partial class SettingsWindow
{
    private static readonly int[] HermesLowAmounts = [0, 2, 5, 10, 20];

    private static readonly (HermesUsageSource Source, string Name)[] HermesSources =
    [
        (HermesUsageSource.Local, "Hermes on this PC"),
        (HermesUsageSource.Remote, "A server's export (SSH)"),
        (HermesUsageSource.Off, "Off (wallet only)"),
    ];

    /// <summary>The Hermes service of the running island; null in demo mode.</summary>
    internal HermesService? Hermes { get; set; }

    internal void RefreshHermes() => UpdateHermes();

    private void InitHermes()
    {
        foreach (var amount in HermesLowAmounts)
        {
            HermesLowPicker.Items.Add(new ComboBoxItem { Content = amount == 0 ? "Off" : DisplayFormat.Usd(amount), Tag = amount });
        }

        foreach (var (source, name) in HermesSources)
        {
            HermesSourcePicker.Items.Add(new ComboBoxItem { Content = name, Tag = source });
        }
    }

    private void LoadHermes(AppSettings s)
    {
        var nearest = HermesLowAmounts.MinBy(a => Math.Abs(a - s.HermesLowBalanceUsd));
        HermesLowPicker.SelectedIndex = Array.IndexOf(HermesLowAmounts, nearest);
        HermesSourcePicker.SelectedIndex = Array.FindIndex(HermesSources, x => x.Source == s.HermesSource);
        HermesRemoteRows.Visibility = s.HermesSource == HermesUsageSource.Remote ? Visibility.Visible : Visibility.Collapsed;
        if (HermesTargetBox.FocusState == FocusState.Unfocused) HermesTargetBox.Text = s.HermesRemoteTarget;
        if (HermesKeyBox.FocusState == FocusState.Unfocused) HermesKeyBox.Text = s.HermesRemoteKeyPath;
        if (HermesPathBox.FocusState == FocusState.Unfocused) HermesPathBox.Text = s.HermesRemotePath;
        foreach (var control in new Control[] { HermesLowPicker, HermesSourcePicker, HermesTargetBox, HermesKeyBox, HermesPathBox })
        {
            control.IsEnabled = s.HermesEnabled;
        }

        UpdateHermes();
    }

    private void UpdateHermes()
    {
        var service = _isDemo ? null : Hermes;
        var enabled = _store.Current.HermesEnabled;
        var status = service?.Status ?? NousStatus.SignedOut;
        (NousStatusTitle.Text, NousStatusText.Text) = (_isDemo, enabled, status) switch
        {
            (true, _, _) => ("Demo mode", "The Hermes column shows made-up numbers. Sign in from the normal Quotalis."),
            (_, false, _) => ("Hermes is off", "Sign in to Nous Portal and Quotalis turns the Hermes column on, or switch Hermes on in the module list above."),
            (_, _, NousStatus.SigningIn) => ("Finish signing in", "Sign in on the Nous page in your browser, check that it shows this code, then connect."),
            (_, _, NousStatus.Expired) => ("Nous sign-in expired", "Sign in again to keep the wallet up to date."),
            (_, _, NousStatus.Unavailable) => ("Nous Portal not reachable", "Quotalis keeps the last balance and tries again by itself."),
            (_, _, NousStatus.UnknownFormat) => ("Nous answered in an unknown format", "Quotalis keeps the last balance and retries later. An update may be needed."),
            (_, _, NousStatus.SignedIn) when service?.Wallet is { } wallet =>
                ("Signed in to Nous Portal", $"{DisplayFormat.Usd(wallet.TotalUsd)} in the wallet, read {DisplayFormat.TimeOfDay(wallet.MeasuredAt.ToLocalTime())}. "
                    + "Every second while Hermes works; after 5 quiet minutes, every 5 minutes; never while the PC is locked."),
            (_, _, NousStatus.SignedIn) => ("Signed in to Nous Portal", "Waiting for the first balance."),
            _ => ("Not signed in", "Sign in to Nous Portal once and the Hermes column shows your wallet: full after a top-up, draining as Hermes spends."),
        };

        var code = status == NousStatus.SigningIn ? service?.PendingCode?.UserCode : null;
        NousCodeText.Text = code ?? string.Empty;
        NousCodeText.Visibility = code is null ? Visibility.Collapsed : Visibility.Visible;
        NousSignInButton.Visibility = status is NousStatus.SignedOut or NousStatus.Expired ? Visibility.Visible : Visibility.Collapsed;
        // Works while Hermes is off too: signing in turns the column on (see OnNousSignIn).
        NousSignInButton.IsEnabled = !_isDemo && service is not null;
        NousCancelButton.Visibility = status == NousStatus.SigningIn ? Visibility.Visible : Visibility.Collapsed;
        NousRefreshButton.Visibility = status is NousStatus.SignedIn or NousStatus.Unavailable or NousStatus.UnknownFormat ? Visibility.Visible : Visibility.Collapsed;
        NousSignOutButton.Visibility = status is NousStatus.SignedIn or NousStatus.Unavailable or NousStatus.UnknownFormat ? Visibility.Visible : Visibility.Collapsed;

        HermesSourceText.Text = (enabled, service?.Usage, service?.UsageProblem) switch
        {
            (true, { } usage, _) => $"Read {DisplayFormat.TimeOfDay(usage.GeneratedAt.ToLocalTime())}"
                + (usage.SchemaVersion is { } v ? $" (Hermes database version {v})" : string.Empty) + ". Numbers only, never your messages.",
            (true, null, { } problem) => problem + ".",
            _ => "Tokens, sessions and the model from Hermes's own records, once a minute. Numbers only, never your messages.",
        };
    }

    private async void OnNousSignIn(object sender, RoutedEventArgs e)
    {
        if (_isDemo || Hermes is not { } service) return;
        NousSignInButton.IsEnabled = false;
        if (!_store.Current.HermesEnabled)
        {
            _store.Update(s => s.HermesEnabled = true);
            Log.Info("settings", "Hermes turned on by the Nous sign-in");
        }

        try
        {
            var code = await service.StartSignInAsync();
            UpdateHermes();
            if (!await Launcher.LaunchUriAsync(code.VerificationUri))
            {
                service.CancelSignIn();
                NousStatusText.Text = "Windows could not open the Nous sign-in page.";
            }
        }
        catch (Exception ex)
        {
            service.CancelSignIn();
            Log.Warn("nous", "Nous sign-in could not start", new { error = ex.GetType().Name });
            NousStatusText.Text = "Nous sign-in could not start. Check the connection, then try again.";
        }

        NousSignInButton.IsEnabled = true;
    }

    private void OnNousCancel(object sender, RoutedEventArgs e)
    {
        Hermes?.CancelSignIn();
        UpdateHermes();
    }

    private void OnNousRefresh(object sender, RoutedEventArgs e) => Hermes?.RefreshNow();

    private void OnNousSignOut(object sender, RoutedEventArgs e)
    {
        Hermes?.SignOut();
        _store.Update(s => s.NousSignedIn = false);
        UpdateHermes();
    }

    private void OnHermesSourceChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || HermesSourcePicker.SelectedItem is not ComboBoxItem { Tag: HermesUsageSource source })
        {
            return;
        }

        _store.Update(s => s.HermesSource = source);
        Log.Info("settings", "Setting changed", new { setting = "HermesSource", value = source.ToString() });
    }

    private void OnHermesLowChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || HermesLowPicker.SelectedItem is not ComboBoxItem { Tag: int amount })
        {
            return;
        }

        _store.Update(s => s.HermesLowBalanceUsd = amount);
        Log.Info("settings", "Setting changed", new { setting = "HermesLowBalanceUsd", value = amount });
    }

    /// <summary>The server, key and path are saved when a box loses focus. They are never logged (they name a host).</summary>
    private void OnHermesRemoteLostFocus(object sender, RoutedEventArgs e)
    {
        var (target, key, path) = (HermesTargetBox.Text.Trim(), HermesKeyBox.Text.Trim(), HermesPathBox.Text.Trim());
        var s = _store.Current;
        if (target == s.HermesRemoteTarget && key == s.HermesRemoteKeyPath && path == s.HermesRemotePath)
        {
            return;
        }

        _store.Update(x =>
        {
            x.HermesRemoteTarget = target;
            x.HermesRemoteKeyPath = key;
            x.HermesRemotePath = path.Length == 0 ? "outbox/hermes-usage.json" : path;
        });
        Log.Info("settings", "Setting changed", new { setting = "HermesRemote" });
    }

    private async void OnHermesKeyBrowse(object sender, RoutedEventArgs e)
    {
        var picker = new FileOpenPicker();
        picker.FileTypeFilter.Add("*");
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
        if (await picker.PickSingleFileAsync() is { } file)
        {
            HermesKeyBox.Text = file.Path;
            OnHermesRemoteLostFocus(sender, e);
        }
    }
}
