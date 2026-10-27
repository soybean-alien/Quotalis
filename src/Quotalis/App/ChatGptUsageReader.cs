using Quotalis.Core.Diagnostics;
using Quotalis.Core.Usage;
using Microsoft.UI.Dispatching;

namespace Quotalis;

internal enum ChatGptUsageStatus { Checking, SignedOut, SigningIn, SignedIn, Unavailable, UnknownFormat, CodexMissing }

/// <summary>Polls the account's complete Work/Codex limit snapshot without sending an AI request.</summary>
internal sealed class ChatGptUsageReader : IDisposable
{
    private readonly CodexAccountClient _client = new();
    private readonly DispatcherQueueTimer _timer;
    private readonly CancellationTokenSource _shutdown = new();
    private bool _enabled;
    private bool _busy;
    private bool _disposed;

    public ChatGptUsageReader(DispatcherQueue dispatcher)
    {
        _timer = dispatcher.CreateTimer();
        _timer.IsRepeating = false;
        _timer.Tick += (_, _) => _ = ReadAsync();
    }

    public event Action? Changed;
    public ChatGptUsageStatus Status { get; private set; } = ChatGptUsageStatus.Checking;
    public ChatGptPlanUsage? Latest { get; private set; }
    public DateTimeOffset? LastRead => Latest?.MeasuredAt;

    public void Configure(bool enabled)
    {
        if (_enabled == enabled) return;
        _enabled = enabled;
        _timer.Stop();
        if (enabled) RefreshNow();
    }

    public void OnActivity()
    {
        if (_enabled && Status == ChatGptUsageStatus.SignedIn
            && (LastRead is null || DateTimeOffset.UtcNow - LastRead >= TimeSpan.FromSeconds(15)))
        {
            Arm(TimeSpan.FromSeconds(4));
        }
    }

    public void RefreshNow()
    {
        if (_enabled && !_disposed) Arm(TimeSpan.FromMilliseconds(1));
    }

    public async Task<string> StartSignInAsync()
    {
        if (!CodexAccountClient.IsAvailable)
        {
            SetStatus(ChatGptUsageStatus.CodexMissing);
            throw new FileNotFoundException("Codex is required for ChatGPT plan sign-in.");
        }

        var url = await _client.StartSignInAsync(_shutdown.Token);
        SetStatus(ChatGptUsageStatus.SigningIn);
        _ = WaitForSignInAsync();
        return url;
    }

    public void CancelSignIn()
    {
        if (Status != ChatGptUsageStatus.SigningIn) return;
        SetStatus(ChatGptUsageStatus.SignedOut);
        Arm(TimeSpan.FromMinutes(5));
    }

    public async Task SignOutAsync()
    {
        _timer.Stop();
        try { await _client.SignOutAsync(_shutdown.Token); }
        catch (Exception ex)
        {
            Log.Warn("chatgpt-usage", "Sign-out failed", new { error = ex.GetType().Name });
            SetStatus(ChatGptUsageStatus.Unavailable);
            Arm(TimeSpan.FromMinutes(5));
            return;
        }
        Latest = null;
        SetStatus(ChatGptUsageStatus.SignedOut);
        Arm(TimeSpan.FromMinutes(5));
    }

    private async Task WaitForSignInAsync()
    {
        for (var i = 0; i < 150 && !_disposed && Status == ChatGptUsageStatus.SigningIn; i++)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(2), _shutdown.Token);
                if (await _client.IsSignedInAsync(_shutdown.Token))
                {
                    SetStatus(ChatGptUsageStatus.SignedIn);
                    RefreshNow();
                    return;
                }
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex) { Log.Warn("chatgpt-usage", "Sign-in check failed", new { error = ex.GetType().Name }); }
        }

        if (Status == ChatGptUsageStatus.SigningIn)
        {
            SetStatus(ChatGptUsageStatus.SignedOut);
            Arm(TimeSpan.FromMinutes(5));
        }
    }

    private async Task ReadAsync()
    {
        if (!_enabled || _busy || _disposed || Status == ChatGptUsageStatus.SigningIn) return;
        _busy = true;
        var next = TimeSpan.FromMinutes(2);
        try
        {
            if (!CodexAccountClient.IsAvailable)
            {
                SetStatus(ChatGptUsageStatus.CodexMissing);
                next = TimeSpan.FromMinutes(5);
                return;
            }

            if (!await _client.IsSignedInAsync(_shutdown.Token))
            {
                Latest = null;
                SetStatus(ChatGptUsageStatus.SignedOut);
                next = TimeSpan.FromMinutes(5);
                return;
            }

            var body = await _client.ReadLimitsAsync(_shutdown.Token);
            var usage = ChatGptPlanUsageParser.Parse(body, DateTimeOffset.UtcNow);
            if (usage is null)
            {
                SetStatus(ChatGptUsageStatus.UnknownFormat);
                next = TimeSpan.FromMinutes(5);
                return;
            }

            Latest = usage;
            SetStatus(ChatGptUsageStatus.SignedIn, force: true);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Log.Warn("chatgpt-usage", "Usage read failed", new { error = ex.GetType().Name });
            SetStatus(ChatGptUsageStatus.Unavailable);
            next = TimeSpan.FromMinutes(5);
        }
        finally
        {
            _busy = false;
            if (_enabled && !_disposed && Status != ChatGptUsageStatus.SigningIn) Arm(next);
        }
    }

    private void Arm(TimeSpan delay)
    {
        _timer.Stop();
        _timer.Interval = delay;
        _timer.Start();
    }

    private void SetStatus(ChatGptUsageStatus status, bool force = false)
    {
        if (Status == status && !force) return;
        Status = status;
        Changed?.Invoke();
    }

    public void Dispose()
    {
        _disposed = true;
        _timer.Stop();
        _shutdown.Cancel();
        _client.Dispose();
        _shutdown.Dispose();
    }
}
