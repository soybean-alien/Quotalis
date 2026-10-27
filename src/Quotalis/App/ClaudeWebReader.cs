using System.Text.Json;
using Quotalis.Core.Diagnostics;
using Quotalis.Core.Plan;
using Microsoft.UI.Dispatching;
using Microsoft.Web.WebView2.Core;

namespace Quotalis;

/// <summary>
/// Reads Claude plan usage from claude.ai with the user's own sign-in (see <see cref="ClaudeWebUsage"/>), in a hidden
/// WebView2 that shares its browser profile with <see cref="ClaudeSignInWindow"/>. The profile (claude.ai's cookies)
/// stays on this PC in <see cref="AppPaths.ClaudeWebProfile"/>; Quotalis never reads the cookie values, never logs
/// an answer, and keeps only the percentages and reset times (<see cref="AppPaths.ClaudePlan"/>). These requests
/// send no message to Claude, so they do not count against the plan limits. Runs on the UI thread.
/// </summary>
internal sealed class ClaudeWebReader : IDisposable
{
    /// <summary>The hidden browser closes when the next read is further away than this, and opens again for it.</summary>
    private static readonly TimeSpan KeepOpen = TimeSpan.FromSeconds(60);

    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(20);

    private static Task<CoreWebView2Environment>? s_environment;

    private readonly DispatcherQueue _dispatcher;
    private readonly IntPtr _parent;
    private readonly PlanUsageStore _store;
    private readonly ClaudeWebSchedule _schedule = new();
    private readonly DispatcherQueueTimer _timer;
    private CoreWebView2Controller? _controller;
    private string? _organization;
    private bool _enabled;
    private bool _busy;
    private bool _disposed;

    public ClaudeWebReader(DispatcherQueue dispatcher, IntPtr parent, PlanUsageStore store)
    {
        _dispatcher = dispatcher;
        _parent = parent;
        _store = store;
        _timer = dispatcher.CreateTimer();
        _timer.IsRepeating = false;
        _timer.Tick += (_, _) => _ = ReadAsync();
    }

    /// <summary>Raised on the UI thread when <see cref="Status"/> or <see cref="LastRead"/> changed.</summary>
    public event Action? Changed;

    public ClaudeWebStatus Status { get; private set; } = ClaudeWebStatus.SignedOut;

    /// <summary>When the last reading arrived from claude.ai.</summary>
    public DateTimeOffset? LastRead { get; private set; }

    /// <summary>The one browser environment for the profile, shared by the reader and the sign-in window.</summary>
    public static Task<CoreWebView2Environment> EnvironmentAsync()
    {
        if (s_environment is null || s_environment.IsFaulted)
        {
            Directory.CreateDirectory(AppPaths.ClaudeWebProfile);
            s_environment = CoreWebView2Environment.CreateWithOptionsAsync(null, AppPaths.ClaudeWebProfile, new CoreWebView2EnvironmentOptions()).AsTask();
        }

        return s_environment;
    }

    /// <summary>Reads while signed in and the Claude module is on; stops otherwise.</summary>
    public void Configure(bool signedIn)
    {
        if (signedIn == _enabled)
        {
            return;
        }

        _enabled = signedIn;
        _organization = null;
        _schedule.Reset();
        if (signedIn)
        {
            SetStatus(ClaudeWebStatus.SignedIn);
            Arm();
        }
        else
        {
            _timer.Stop();
            CloseBrowser();
            SetStatus(ClaudeWebStatus.SignedOut);
        }
    }

    /// <summary>The sign-in window finished with a signed-in profile: read at once, also after an expired sign-in.</summary>
    public void OnSignedIn()
    {
        _enabled = true;
        _organization = null;
        _schedule.Reset();
        SetStatus(ClaudeWebStatus.SignedIn, force: true);
        Arm();
    }

    /// <summary>Claude Code wrote to a session (a reply landed): read again in a few seconds.</summary>
    public void OnActivity()
    {
        if (_enabled && Status == ClaudeWebStatus.SignedIn)
        {
            _schedule.OnActivity(DateTimeOffset.UtcNow);
            Arm();
        }
    }

    /// <summary>Read now (Settings, "Refresh now", and right after signing in).</summary>
    public void RefreshNow()
    {
        if (_enabled)
        {
            _schedule.Reset();
            Arm();
        }
    }

    /// <summary>Forgets the sign-in: every cookie and all other data of the profile is cleared.</summary>
    public async Task SignOutAsync()
    {
        Configure(false);
        try
        {
            var environment = await EnvironmentAsync();
            var controller = await environment.CreateCoreWebView2ControllerAsync(CoreWebView2ControllerWindowReference.CreateFromWindowHandle((ulong)_parent));
            try
            {
                await controller.CoreWebView2.Profile.ClearBrowsingDataAsync();
            }
            finally
            {
                controller.Close();
            }

            Log.Info("claude-web", "Signed out of claude.ai, browser data cleared");
        }
        catch (Exception ex)
        {
            Log.Warn("claude-web", "Sign-out could not clear the browser data", new { error = ex.GetType().Name });
        }
    }

    public void Dispose()
    {
        _disposed = true;
        _timer.Stop();
        CloseBrowser();
    }

    private void Arm()
    {
        if (!_enabled || _disposed || _busy || Status == ClaudeWebStatus.Expired)
        {
            return;
        }

        var now = DateTimeOffset.UtcNow;
        var due = _schedule.Next(now) - now;
        _timer.Stop();
        _timer.Interval = due > TimeSpan.Zero ? due : TimeSpan.FromMilliseconds(1);
        _timer.Start();
        if (due > KeepOpen)
        {
            CloseBrowser();
        }
    }

    private async Task ReadAsync()
    {
        if (!_enabled || _busy || _disposed)
        {
            return;
        }

        _busy = true;
        var now = DateTimeOffset.UtcNow;
        var succeeded = false;
        try
        {
            var web = await BrowserAsync();
            if (_organization is null)
            {
                var (status, body) = await GetAsync(web, ClaudeWebUsage.OrganizationsUrl);
                if (Refused(status, body) || status != 200)
                {
                    return;
                }

                var cookies = await web.CookieManager.GetCookiesAsync(ClaudeWebUsage.Origin);
                var lastActive = cookies.FirstOrDefault(c => c.Name == ClaudeWebUsage.ActiveOrganizationCookie)?.Value;
                _organization = ClaudeWebUsage.PickOrganization(body, lastActive);
                if (_organization is null)
                {
                    Log.Warn("claude-web", "claude.ai organizations not in a known format");
                    SetStatus(ClaudeWebStatus.FormatChanged);
                    return;
                }
            }

            var (usageStatus, usageBody) = await GetAsync(web, ClaudeWebUsage.UsageUrl(_organization)!);
            if (Refused(usageStatus, usageBody))
            {
                return;
            }

            if (usageStatus != 200)
            {
                _organization = null; // look it up again next time (it may have changed)
                Log.Info("claude-web", "claude.ai usage not read", new { status = usageStatus });
                return;
            }

            if (ClaudeWebUsage.Parse(usageBody, now) is not { } usage)
            {
                Log.Warn("claude-web", "claude.ai usage not in a known format");
                SetStatus(ClaudeWebStatus.FormatChanged);
                return;
            }

            _store.Save(usage);
            succeeded = true;
            LastRead = now;
            SetStatus(ClaudeWebStatus.SignedIn, force: true);
        }
        catch (Exception ex)
        {
            // Type only: an answer or a message could carry account details.
            Log.Warn("claude-web", "claude.ai usage read failed", new { error = ex.GetType().Name });
        }
        finally
        {
            _busy = false;
            _schedule.OnAttempt(now, succeeded);
            Arm();
        }
    }

    private bool Refused(int status, string body)
    {
        if (!ClaudeWebUsage.IsSignInRefused(status, body))
        {
            if (status != 200)
            {
                Log.Info("claude-web", "claude.ai not reachable", new { status });
            }

            return false;
        }

        Log.Info("claude-web", "claude.ai sign-in no longer accepted", new { status });
        _organization = null;
        _timer.Stop();
        CloseBrowser();
        SetStatus(ClaudeWebStatus.Expired);
        return true;
    }

    private async Task<CoreWebView2> BrowserAsync()
    {
        if (_controller is null)
        {
            var environment = await EnvironmentAsync();
            _controller = await environment.CreateCoreWebView2ControllerAsync(CoreWebView2ControllerWindowReference.CreateFromWindowHandle((ulong)_parent));
            _controller.IsVisible = false;
            var settings = _controller.CoreWebView2.Settings;
            settings.IsPasswordAutosaveEnabled = false;
            settings.IsGeneralAutofillEnabled = false;
            settings.AreDefaultContextMenusEnabled = false;
        }

        return _controller.CoreWebView2;
    }

    private void CloseBrowser()
    {
        if (_controller is null || _busy)
        {
            return;
        }

        try
        {
            _controller.Close();
        }
        catch (Exception ex)
        {
            Log.Debug("claude-web", "Hidden browser close failed", new { error = ex.GetType().Name });
        }

        _controller = null;
    }

    /// <summary>Opens one claude.ai address in the hidden browser: its HTTP status and the text it shows.</summary>
    private static async Task<(int Status, string Body)> GetAsync(CoreWebView2 web, string url)
    {
        var done = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnCompleted(CoreWebView2 sender, CoreWebView2NavigationCompletedEventArgs e) =>
            done.TrySetResult(e.HttpStatusCode != 0 ? e.HttpStatusCode : e.IsSuccess ? 200 : -1);

        web.NavigationCompleted += OnCompleted;
        try
        {
            web.Navigate(url);
            var status = await done.Task.WaitAsync(RequestTimeout);
            // A JSON answer is shown as text in one <pre>; the page may add a pretty-print control next to it.
            var raw = await web.ExecuteScriptAsync("(document.querySelector('body > pre') || document.body || {}).innerText || ''");
            using var doc = JsonDocument.Parse(raw);
            return (status, doc.RootElement.ValueKind == JsonValueKind.String ? doc.RootElement.GetString() ?? string.Empty : string.Empty);
        }
        finally
        {
            web.NavigationCompleted -= OnCompleted;
        }
    }

    private void SetStatus(ClaudeWebStatus status, bool force = false)
    {
        if (status == Status && !force)
        {
            return;
        }

        if (status != Status)
        {
            Log.Info("claude-web", "claude.ai status", new { status = status.ToString() });
        }

        Status = status;
        Changed?.Invoke();
    }
}
