using Claudex.Core.Calendar;
using Claudex.Core.Diagnostics;
using Claudex.Core.Git;
using Claudex.Core.Modules;
using Claudex.Core.Plan;
using Claudex.Core.Settings;
using Claudex.UI.Components;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.Storage.Pickers;
using Windows.System;
using Rectangle = Microsoft.UI.Xaml.Shapes.Rectangle;

namespace Claudex.UI.Settings;

/// <summary>Settings, Modules section: order and switches for every module, Git repositories, calendars.</summary>
public sealed partial class SettingsWindow
{
    private static string Describe(ModuleId module) => module switch
    {
        ModuleId.Claude => "Token usage and API equivalent from local Claude Code logs.",
        ModuleId.Codex => "Token usage, rate limits and API equivalent from local Codex logs.",
        ModuleId.Music => "What's playing in Spotify, Apple Music, browsers and other media apps.",
        ModuleId.Git => "Branch, changes and last commit of your current project. Local only.",
        ModuleId.GitHub => "Notifications, pull requests and CI through the GitHub CLI. Read-only.",
        ModuleId.Focus => "A focus timer with 25, 50 and 90 minute presets.",
        ModuleId.Calendar => "Your next event from a calendar link or an .ics file.",
        ModuleId.Tasks => "A small to-do list, stored on this PC.",
        ModuleId.System => "CPU, memory and battery. Speaks up only when something needs attention.",
        ModuleId.Awake => "Keeps this PC awake for a set time or until you end it, also with the lid closed.",
        _ => string.Empty,
    };

    private void BuildModuleRows(AppSettings s)
    {
        ModuleRows.Children.Clear();
        var order = s.Order;
        for (var i = 0; i < order.Count; i++)
        {
            var module = order[i];
            var name = ModuleCatalog.DisplayName(module);
            if (i > 0)
            {
                ModuleRows.Children.Add(Divider());
            }

            var row = new Grid { Style = Res("SettingsRowStyle"), ColumnSpacing = 12 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var icon = new Border
            {
                Width = 32,
                Height = 32,
                CornerRadius = new CornerRadius(8),
                Background = ThemeBrush("SettingsSegmentBrush"),
                VerticalAlignment = VerticalAlignment.Center,
                Child = new ModuleIcon { Module = module, Foreground = ThemeBrush("SettingsTextPrimaryBrush"), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
            };
            row.Children.Add(icon);

            var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            text.Children.Add(new TextBlock { Text = name, Style = Res("SettingsRowTitleStyle") });
            text.Children.Add(new TextBlock { Text = Describe(module), Style = Res("SettingsRowDescriptionStyle") });
            Grid.SetColumn(text, 1);
            row.Children.Add(text);

            var moves = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
            moves.Children.Add(MoveButton(module, -1, "", $"Move {name} up", i > 0));
            moves.Children.Add(MoveButton(module, 1, "", $"Move {name} down", i < order.Count - 1));
            Grid.SetColumn(moves, 2);
            row.Children.Add(moves);

            var toggle = new ToggleSwitch
            {
                Style = Res("SettingsToggleStyle"),
                IsOn = s.IsEnabled(module),
                Tag = "Module:" + module,
            };
            AutomationProperties.SetName(toggle, name);
            toggle.Toggled += OnToggle;
            Grid.SetColumn(toggle, 3);
            row.Children.Add(toggle);

            ModuleRows.Children.Add(row);
        }
    }

    private Button MoveButton(ModuleId module, int delta, string glyph, string name, bool enabled)
    {
        var button = new Button
        {
            Content = new FontIcon { Glyph = glyph, FontSize = 12 },
            Width = 32,
            Height = 32,
            Padding = new Thickness(0),
            Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Transparent),
            BorderThickness = new Thickness(0),
            IsEnabled = enabled,
            Opacity = enabled ? 1 : 0, // the ends: keep the layout, show nothing
        };
        AutomationProperties.SetName(button, name);
        ToolTipService.SetToolTip(button, delta < 0 ? "Move up" : "Move down");
        button.Click += (_, _) =>
        {
            _store.Update(s => s.ModuleOrder = ModuleCatalog.Move(s.Order, module, delta).Select(m => m.ToString()).ToList());
            Log.Info("settings", "Module moved", new { module = module.ToString(), delta });
        };
        return button;
    }

    // Claude plan usage --------------------------------------------------------------------------------

    /// <summary>The claude.ai reader of the running island; null in demo mode.</summary>
    internal ClaudeWebReader? ClaudeWeb { get; set; }

    private ClaudeSignInWindow? _signIn;

    /// <summary>Called when the claude.ai sign-in changed state or read new numbers.</summary>
    internal void RefreshPlanConnection() => UpdatePlanConnection();

    /// <summary>Shows the claude.ai sign-in and the optional status line, each with what it delivers right now.</summary>
    private void UpdatePlanConnection()
    {
        var web = _isDemo ? ClaudeWebStatus.SignedOut : ClaudeWeb?.Status ?? ClaudeWebStatus.SignedOut;
        var signedIn = !_isDemo && _store.Current.ClaudeWebSignedIn;
        var usage = _isDemo ? null : new PlanUsageStore(AppPaths.ClaudePlan).Load();
        var now = DateTimeOffset.UtcNow;
        var lastRead = ClaudeWeb?.LastRead is { } at ? Core.Formatting.DisplayFormat.Ago(now - at) : null;

        (PlanStatusTitle.Text, PlanStatusText.Text) = (_isDemo, signedIn, web) switch
        {
            (true, _, _) => ("Not available in demo mode", "Demo mode never signs in to claude.ai."),
            (_, true, ClaudeWebStatus.Expired) => ("Your claude.ai sign-in expired", "The island shows no Claude percentage until you sign in again."),
            (_, true, ClaudeWebStatus.FormatChanged) => ("claude.ai answered in an unknown format", "Claudex shows nothing rather than a wrong number, and keeps trying every few minutes. An update of Claudex may be needed."),
            (_, true, _) when lastRead is not null && usage is not null => ("Signed in to claude.ai", $"{Usage(usage, now)}. Updated {lastRead}. Refreshes a few seconds after each Claude reply, and every 2 minutes otherwise."),
            (_, true, _) => ("Signed in to claude.ai", "Waiting for the first reading. It arrives in a few seconds."),
            _ => ("Not signed in", "Sign in to claude.ai once and Claudex reads your 5-hour and weekly usage the way claude.ai's own Usage page does."),
        };
        PlanSignInButton.Content = signedIn && web == ClaudeWebStatus.Expired ? "Sign in again" : "Sign in to claude.ai";
        PlanSignInButton.Visibility = !signedIn || web == ClaudeWebStatus.Expired ? Visibility.Visible : Visibility.Collapsed;
        PlanSignInButton.IsEnabled = !_isDemo && ClaudeWeb is not null && _signIn is null;
        PlanRefreshButton.Visibility = signedIn && web != ClaudeWebStatus.Expired ? Visibility.Visible : Visibility.Collapsed;
        PlanSignOutButton.Visibility = signedIn ? Visibility.Visible : Visibility.Collapsed;

        var line = _isDemo ? PlanConnection.NotConnected : ClaudeStatusLineSetup.Inspect(ClaudeStatusLineSetup.SettingsPath());
        StatusLineText.Text = (_isDemo, line) switch
        {
            (true, _) => "Demo mode never changes Claude Code's settings.",
            (_, PlanConnection.Connected) => "Connected. Claude Code terminal sessions also hand over usage after every reply.",
            (_, PlanConnection.OtherStatusLine) => "Claude Code already runs your own status line, which Claudex leaves alone. To add terminal readings, pipe its input to: " + ClaudeStatusLineSetup.CommandFor(Environment.ProcessPath ?? "Claudex.exe"),
            (_, PlanConnection.Unreadable) => "Claude Code's settings.json could not be read, so nothing was changed.",
            _ => "Also takes usage from Claude Code terminal sessions, after every reply. Desktop sessions don't need it.",
        };
        PlanConnectButton.Visibility = line == PlanConnection.Connected ? Visibility.Collapsed : Visibility.Visible;
        PlanConnectButton.IsEnabled = !_isDemo && line == PlanConnection.NotConnected && Environment.ProcessPath is not null;
        PlanDisconnectButton.Visibility = line == PlanConnection.Connected ? Visibility.Visible : Visibility.Collapsed;
    }

    private static string Usage(PlanUsage usage, DateTimeOffset now)
    {
        static string Percent(PlanWindow? w) => w is null ? "--" : $"{Math.Round(w.UsedPercent):0}%";
        return $"5-hour {Percent(usage.CurrentAt(now))} · weekly {Percent(usage.WeeklyAt(now))}";
    }

    private void OnClaudeSignIn(object sender, RoutedEventArgs e)
    {
        if (_isDemo || ClaudeWeb is null || _signIn is not null)
        {
            return;
        }

        _signIn = new ClaudeSignInWindow();
        _signIn.Finished += signedIn =>
        {
            _signIn = null;
            if (signedIn)
            {
                _store.Update(s => s.ClaudeWebSignedIn = true);
                ClaudeWeb?.OnSignedIn();
            }

            UpdatePlanConnection();
        };
        _signIn.Activate();
        UpdatePlanConnection();
    }

    private void OnClaudeRefresh(object sender, RoutedEventArgs e) => ClaudeWeb?.RefreshNow();

    // ChatGPT plan usage -----------------------------------------------------------------------------

    /// <summary>The account reader of the running island; null in demo mode.</summary>
    internal ChatGptUsageReader? ChatGptUsage { get; set; }

    internal void RefreshChatGptUsage() => UpdateChatGptUsage();

    private void UpdateChatGptUsage()
    {
        var reader = _isDemo ? null : ChatGptUsage;
        var status = reader?.Status ?? ChatGptUsageStatus.Checking;
        var now = DateTimeOffset.UtcNow;
        (ChatGptStatusTitle.Text, ChatGptStatusText.Text) = (_isDemo, status) switch
        {
            (true, _) => ("Not available in demo mode", "Demo mode never signs in to ChatGPT."),
            (_, ChatGptUsageStatus.CodexMissing) => ("Codex is needed", "Install the Codex desktop app or CLI to connect ChatGPT plan usage."),
            (_, ChatGptUsageStatus.SignedOut) => ("Not signed in", "Sign in with ChatGPT to sync the shared Work and Codex plan allowance, including five-hour and weekly reset times."),
            (_, ChatGptUsageStatus.SigningIn) => ("Finish signing in", "Complete ChatGPT sign-in in your browser. Claudex will refresh when it finishes."),
            (_, ChatGptUsageStatus.Unavailable) => ("ChatGPT usage is unavailable", "The last reading is shown below, if one exists. Use Refresh now to try again."),
            (_, ChatGptUsageStatus.UnknownFormat) => ("ChatGPT usage has an unknown format", "Claudex keeps the last valid reading and retries later. An update may be needed."),
            (_, ChatGptUsageStatus.SignedIn) when reader?.LastRead is { } at => ("Signed in with ChatGPT", $"Updated {Core.Formatting.DisplayFormat.Ago(now - at)}. Refreshes after Codex activity and every 2 minutes otherwise."),
            (_, ChatGptUsageStatus.SignedIn) => ("Signed in with ChatGPT", "Waiting for the first usage reading."),
            _ => ("Checking ChatGPT", "Checking this PC's ChatGPT plan connection."),
        };

        ChatGptSignInButton.Visibility = status is ChatGptUsageStatus.SignedOut or ChatGptUsageStatus.CodexMissing ? Visibility.Visible : Visibility.Collapsed;
        ChatGptSignInButton.IsEnabled = !_isDemo && reader is not null && status != ChatGptUsageStatus.CodexMissing;
        ChatGptRefreshButton.Visibility = status is ChatGptUsageStatus.SignedIn or ChatGptUsageStatus.Unavailable or ChatGptUsageStatus.UnknownFormat ? Visibility.Visible : Visibility.Collapsed;
        ChatGptSignOutButton.Visibility = status is ChatGptUsageStatus.SignedIn or ChatGptUsageStatus.SigningIn or ChatGptUsageStatus.Unavailable or ChatGptUsageStatus.UnknownFormat ? Visibility.Visible : Visibility.Collapsed;

        ChatGptBucketRows.Children.Clear();
        foreach (var bucket in reader?.Latest?.Buckets ?? [])
        {
            ChatGptBucketRows.Children.Add(new TextBlock
            {
                Text = $"{bucket.Name}: {Window(bucket.Current, "5-hour", now)} · {Window(bucket.Weekly, "weekly", now)}",
                Style = Res("SettingsRowDescriptionStyle"),
                TextWrapping = TextWrapping.Wrap,
            });
        }
    }

    private static string Window(Claudex.Core.Usage.CodexRateLimit? limit, string label, DateTimeOffset now)
    {
        if (limit is null) return $"{label} unavailable";
        if (limit.ResetsAt is { } reset && reset <= now) return $"{label} 0% (reset; starts on next request)";
        var percent = $"{Math.Round(limit.UsedPercent):0}%";
        return limit.ResetsAt is { } at ? $"{label} {percent}, resets {at.ToLocalTime():ddd MMM d, h:mm tt}" : $"{label} {percent}, reset time unavailable";
    }

    private async void OnChatGptSignIn(object sender, RoutedEventArgs e)
    {
        if (_isDemo || ChatGptUsage is not { } reader) return;
        ChatGptSignInButton.IsEnabled = false;
        try
        {
            var url = await reader.StartSignInAsync();
            if (!await Launcher.LaunchUriAsync(new Uri(url)))
            {
                reader.CancelSignIn();
                ChatGptStatusText.Text = "Windows could not open the ChatGPT sign-in page.";
            }
            else UpdateChatGptUsage();
        }
        catch (Exception ex)
        {
            reader.CancelSignIn();
            Log.Warn("chatgpt-usage", "Sign-in could not start", new { error = ex.GetType().Name });
            ChatGptStatusText.Text = "ChatGPT sign-in could not start. Check that Codex is installed, then try again.";
        }
        ChatGptSignInButton.IsEnabled = true;
    }

    private void OnChatGptRefresh(object sender, RoutedEventArgs e) => ChatGptUsage?.RefreshNow();

    private async void OnChatGptSignOut(object sender, RoutedEventArgs e)
    {
        if (ChatGptUsage is { } reader) await reader.SignOutAsync();
        UpdateChatGptUsage();
    }

    private async void OnClaudeSignOut(object sender, RoutedEventArgs e)
    {
        _store.Update(s => s.ClaudeWebSignedIn = false);
        if (ClaudeWeb is { } web)
        {
            await web.SignOutAsync();
        }

        UpdatePlanConnection();
    }

    private void OnConnectPlan(object sender, RoutedEventArgs e)
    {
        if (_isDemo || Environment.ProcessPath is not { } exe)
        {
            return;
        }

        try
        {
            var result = ClaudeStatusLineSetup.Connect(ClaudeStatusLineSetup.SettingsPath(), exe);
            Log.Info("settings", "Claude status line connect", new { result = result.ToString() });
        }
        catch (Exception ex)
        {
            Log.Warn("settings", "Claude status line could not be connected", new { error = ex.GetType().Name });
        }

        UpdatePlanConnection();
    }

    private void OnDisconnectPlan(object sender, RoutedEventArgs e)
    {
        try
        {
            var result = ClaudeStatusLineSetup.Disconnect(ClaudeStatusLineSetup.SettingsPath());
            Log.Info("settings", "Claude status line disconnect", new { result = result.ToString() });
        }
        catch (Exception ex)
        {
            Log.Warn("settings", "Claude status line could not be disconnected", new { error = ex.GetType().Name });
        }

        UpdatePlanConnection();
    }

    // Git repositories -------------------------------------------------------------------------------

    private void BuildRepositoryRows(AppSettings s)
    {
        RepoRows.Children.Clear();
        foreach (var path in s.GitRepositories)
        {
            RepoRows.Children.Add(ListRow(Path.GetFileName(path.TrimEnd('\\', '/')), path, "Remove repository", () =>
                _store.Update(x => x.GitRepositories.RemoveAll(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase)))));
            RepoRows.Children.Add(Divider());
        }
    }

    private async void OnAddRepository(object sender, RoutedEventArgs e)
    {
        try
        {
            var picker = new FolderPicker { SuggestedStartLocation = PickerLocationId.DocumentsLibrary };
            picker.FileTypeFilter.Add("*");
            WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
            var folder = await picker.PickSingleFolderAsync();
            if (folder is null)
            {
                return;
            }

            var root = GitStatusParser.FindRepositoryRoot(folder.Path);
            if (root is null)
            {
                await ShowMessageAsync("Not a Git repository", "Choose the folder of a Git repository (it contains a .git folder), or a folder inside one.");
                return;
            }

            _store.Update(s =>
            {
                if (!s.GitRepositories.Contains(root, StringComparer.OrdinalIgnoreCase))
                {
                    s.GitRepositories.Add(root);
                }
            });
        }
        catch (Exception ex)
        {
            Log.Warn("settings", "Repository could not be added", new { error = ex.GetType().Name });
        }
    }

    // Calendars -------------------------------------------------------------------------------------

    private void BuildCalendarRows(AppSettings s)
    {
        CalendarRows.Children.Clear();
        foreach (var feed in s.CalendarFeeds)
        {
            // Links contain a private token: show the provider name, never the address.
            var detail = feed.FilePath ?? "Private link, kept in Windows Credential Manager";
            var id = feed.Id;
            CalendarRows.Children.Add(ListRow(feed.Name, detail, "Remove calendar", () =>
                _store.Update(x => CalendarFeeds.Remove(x, _secrets, id))));
            CalendarRows.Children.Add(Divider());
        }
    }

    private void OnCalendarLinkChanged(object sender, TextChangedEventArgs e) =>
        AddCalendarButton.IsEnabled = IcsCalendarSource.IsValidLocation(CalendarLinkBox.Text) && IcsCalendarSource.IsWebAddress(CalendarLinkBox.Text.Trim());

    private void OnCalendarLinkKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Enter && AddCalendarButton.IsEnabled)
        {
            OnAddCalendarLink(sender, e);
            e.Handled = true;
        }
    }

    private void OnAddCalendarLink(object sender, RoutedEventArgs e)
    {
        var link = CalendarLinkBox.Text.Trim();
        if (!IcsCalendarSource.IsValidLocation(link))
        {
            return;
        }

        AddCalendar(link);
        CalendarLinkBox.Text = string.Empty;
    }

    private async void OnAddCalendarFile(object sender, RoutedEventArgs e)
    {
        try
        {
            var picker = new FileOpenPicker { SuggestedStartLocation = PickerLocationId.DocumentsLibrary };
            picker.FileTypeFilter.Add(".ics");
            WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
            var file = await picker.PickSingleFileAsync();
            if (file is not null)
            {
                AddCalendar(file.Path);
            }
        }
        catch (Exception ex)
        {
            Log.Warn("settings", "Calendar file could not be added", new { error = ex.GetType().Name });
        }
    }

    private void AddCalendar(string location)
    {
        _store.Update(s =>
        {
            var duplicate = CalendarFeeds.Resolve(s.CalendarFeeds, _secrets).Contains(location, StringComparer.Ordinal);
            if (!duplicate)
            {
                CalendarFeeds.Add(s, _secrets, location);
            }

            s.CalendarEnabled = true;
        });
        Log.Info("settings", "Calendar added", new { source = IcsCalendarSource.DisplayName(location) });
    }

    // Shared -----------------------------------------------------------------------------------------

    private Grid ListRow(string title, string detail, string removeName, Action remove)
    {
        var row = new Grid { Style = Res("SettingsRowStyle"), ColumnSpacing = 12 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock { Text = title, Style = Res("SettingsRowTitleStyle") });
        text.Children.Add(new TextBlock { Text = detail, Style = Res("SettingsRowDescriptionStyle"), TextTrimming = TextTrimming.CharacterEllipsis, TextWrapping = TextWrapping.NoWrap });
        row.Children.Add(text);
        var button = new Button { Content = "Remove", VerticalAlignment = VerticalAlignment.Center };
        AutomationProperties.SetName(button, $"{removeName} {title}");
        button.Click += (_, _) => remove();
        Grid.SetColumn(button, 1);
        row.Children.Add(button);
        return row;
    }

    private Rectangle Divider() => new() { Style = Res("SettingsDividerStyle") };

    /// <summary>Settings styles live in this window's resources, not the application's.</summary>
    private Style Res(string key) => (Style)(Root.Resources.TryGetValue(key, out var value) ? value : Application.Current.Resources[key]);

    private async Task ShowMessageAsync(string title, string message)
    {
        var dialog = new ContentDialog
        {
            Title = title,
            Content = message,
            CloseButtonText = "OK",
            XamlRoot = Root.XamlRoot,
            RequestedTheme = Root.ActualTheme,
        };
        await dialog.ShowAsync();
    }
}
