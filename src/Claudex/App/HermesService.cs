using System.Diagnostics;
using System.Text.Json;
using Claudex.Core.Diagnostics;
using Claudex.Core.Hermes;
using Claudex.Core.Settings;
using Microsoft.UI.Dispatching;

namespace Claudex;

internal enum NousStatus { SignedOut, SigningIn, SignedIn, Expired, Unavailable, UnknownFormat }

/// <summary>
/// The Hermes column's data: the Nous wallet (Claudex's own device-code sign-in, read on <see cref="NousPollSchedule"/>)
/// and Hermes's token use (a local <c>state.db</c> or the server export, once a minute). Reading the wallet is a free
/// account read; Claudex never calls an inference address. Tokens, answers and account details are never logged.
/// Runs on the UI thread; file and network work is awaited.
/// </summary>
internal sealed class HermesService : IDisposable
{
    private static readonly TimeSpan UsageInterval = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan RefreshAhead = TimeSpan.FromMinutes(2);
    private static readonly HttpClient Http = CreateHttp();

    private readonly NousPortalClient _portal = new(Http);
    private readonly NousTokenStore _store;
    private readonly string _gaugePath;
    private readonly string _usageCache;
    private readonly NousPollSchedule _schedule = new();
    private readonly DispatcherQueueTimer _walletTimer;
    private readonly DispatcherQueueTimer _usageTimer;
    private readonly CancellationTokenSource _shutdown = new();
    private NousTokens? _tokens;
    private CancellationTokenSource? _signIn;
    private bool _enabled;
    private bool _walletBusy;
    private bool _usageBusy;
    private bool _disposed;
    private HermesUsageSource _source = HermesUsageSource.Off;
    private string _remoteTarget = "";
    private string _remoteKey = "";
    private string _remotePath = "";

    public HermesService(DispatcherQueue dispatcher, NousTokenStore store, string gaugePath, string usageCache)
    {
        _store = store;
        _gaugePath = gaugePath;
        _usageCache = usageCache;
        _walletTimer = dispatcher.CreateTimer();
        _walletTimer.IsRepeating = false;
        _walletTimer.Tick += (_, _) => _ = ReadWalletAsync();
        _usageTimer = dispatcher.CreateTimer();
        _usageTimer.IsRepeating = false;
        _usageTimer.Tick += (_, _) => _ = ReadUsageAsync();
        Gauge = LoadGauge();
    }

    /// <summary>Raised on the UI thread when anything below changed.</summary>
    public event Action? Changed;

    public NousStatus Status { get; private set; } = NousStatus.SignedOut;

    /// <summary>The code to approve on the portal while <see cref="NousStatus.SigningIn"/>.</summary>
    public NousDeviceCode? PendingCode { get; private set; }

    public NousWalletReading? Wallet { get; private set; }

    public WalletGauge? Gauge { get; private set; }

    /// <summary>Reads run every second right now (Hermes is working or the balance just moved).</summary>
    public bool IsFast => _schedule.IsFast(DateTimeOffset.UtcNow) && !_schedule.Paused;

    public bool IsPaused => _schedule.Paused;

    public HermesUsage? Usage { get; private set; }

    /// <summary>Why token use is missing ("No Hermes install on this PC"), or null.</summary>
    public string? UsageProblem { get; private set; }

    public void Configure(AppSettings settings)
    {
        var enabled = settings.HermesEnabled;
        var sourceChanged = settings.HermesSource != _source || settings.HermesRemoteTarget != _remoteTarget
            || settings.HermesRemoteKeyPath != _remoteKey || settings.HermesRemotePath != _remotePath;
        _source = settings.HermesSource;
        _remoteTarget = settings.HermesRemoteTarget.Trim();
        _remoteKey = settings.HermesRemoteKeyPath.Trim();
        _remotePath = settings.HermesRemotePath.Trim();

        if (enabled != _enabled)
        {
            _enabled = enabled;
            if (enabled)
            {
                _tokens ??= _store.Load();
                // A sign-in started from Settings may already be waiting (it turns Hermes on): keep it.
                if (Status != NousStatus.SigningIn)
                {
                    SetStatus(_tokens is null ? NousStatus.SignedOut : NousStatus.SignedIn);
                }

                _schedule.Wake(DateTimeOffset.UtcNow);
                ArmWallet();
                ArmUsage(TimeSpan.FromMilliseconds(1));
            }
            else
            {
                _walletTimer.Stop();
                _usageTimer.Stop();
                CancelSignIn();
            }
        }
        else if (enabled && sourceChanged)
        {
            Usage = null;
            UsageProblem = null;
            ArmUsage(TimeSpan.FromMilliseconds(1));
            Changed?.Invoke();
        }
    }

    /// <summary>The PC was locked or went to sleep (false), or came back (true). No wallet reads while away.</summary>
    public void SetSessionActive(bool active)
    {
        _schedule.Paused = !active;
        if (active)
        {
            _schedule.Wake(DateTimeOffset.UtcNow);
        }

        ArmWallet();
        Changed?.Invoke();
    }

    /// <summary>Settings' "Refresh now": read the wallet and the usage at once, then fast for five minutes.</summary>
    public void RefreshNow()
    {
        _schedule.Wake(DateTimeOffset.UtcNow);
        ArmWallet(force: true);
        ArmUsage(TimeSpan.FromMilliseconds(1));
    }

    /// <summary>Starts the device sign-in; the caller opens <see cref="NousDeviceCode.VerificationUri"/> in the browser.</summary>
    public async Task<NousDeviceCode> StartSignInAsync()
    {
        CancelSignIn();
        var code = await _portal.RequestCodeAsync(DateTimeOffset.UtcNow, _shutdown.Token);
        PendingCode = code;
        _signIn = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token);
        SetStatus(NousStatus.SigningIn, force: true);
        _ = WaitForApprovalAsync(code, _signIn.Token);
        return code;
    }

    public void CancelSignIn()
    {
        _signIn?.Cancel();
        _signIn = null;
        if (Status == NousStatus.SigningIn)
        {
            PendingCode = null;
            SetStatus(_tokens is null ? NousStatus.SignedOut : NousStatus.SignedIn);
        }
    }

    /// <summary>Forgets the sign-in on this PC. (Nous may still list the connection under Connected Agents.)</summary>
    public void SignOut()
    {
        CancelSignIn();
        _tokens = null;
        _store.Delete();
        Wallet = null;
        _walletTimer.Stop();
        Log.Info("nous", "Signed out of Nous Portal");
        SetStatus(NousStatus.SignedOut, force: true);
    }

    private async Task WaitForApprovalAsync(NousDeviceCode code, CancellationToken cancel)
    {
        var interval = code.Interval;
        try
        {
            while (!cancel.IsCancellationRequested && DateTimeOffset.UtcNow < code.ExpiresAt)
            {
                await Task.Delay(interval, cancel);
                var (result, tokens) = await _portal.PollAsync(code, DateTimeOffset.UtcNow, cancel);
                switch (result)
                {
                    case NousPollResult.Granted:
                        _tokens = tokens;
                        _store.Save(tokens!);
                        PendingCode = null;
                        Log.Info("nous", "Signed in to Nous Portal");
                        SetStatus(NousStatus.SignedIn, force: true);
                        _schedule.Wake(DateTimeOffset.UtcNow);
                        ArmWallet(force: true);
                        return;
                    case NousPollResult.SlowDown:
                        interval = TimeSpan.FromSeconds(Math.Min(interval.TotalSeconds + 1, 30));
                        break;
                    case NousPollResult.Denied or NousPollResult.Expired or NousPollResult.Failed:
                        Log.Info("nous", "Nous sign-in ended", new { result = result.ToString() });
                        PendingCode = null;
                        SetStatus(_tokens is null ? NousStatus.SignedOut : NousStatus.SignedIn, force: true);
                        return;
                }
            }
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            Log.Warn("nous", "Nous sign-in check failed", new { error = ex.GetType().Name });
        }

        if (Status == NousStatus.SigningIn)
        {
            PendingCode = null;
            SetStatus(_tokens is null ? NousStatus.SignedOut : NousStatus.SignedIn, force: true);
        }
    }

    // The wallet ------------------------------------------------------------------------------------------

    private void ArmWallet(bool force = false)
    {
        _walletTimer.Stop();
        if (!_enabled || _disposed || _tokens is null || Status is NousStatus.Expired or NousStatus.SigningIn)
        {
            return;
        }

        var now = DateTimeOffset.UtcNow;
        if (force)
        {
            _walletTimer.Interval = TimeSpan.FromMilliseconds(1);
            _walletTimer.Start();
            return;
        }

        if (_schedule.Next(now) is { } next)
        {
            var due = next - now;
            _walletTimer.Interval = due > TimeSpan.Zero ? due : TimeSpan.FromMilliseconds(1);
            _walletTimer.Start();
        }
    }

    private async Task ReadWalletAsync()
    {
        if (!_enabled || _walletBusy || _disposed || _tokens is null || _schedule.Paused)
        {
            return;
        }

        _walletBusy = true;
        var now = DateTimeOffset.UtcNow;
        try
        {
            if (_tokens.ExpiresAt - now < RefreshAhead && !await RenewAsync(now))
            {
                return;
            }

            var answer = await _portal.ReadAccountAsync(_tokens!.AccessToken, _shutdown.Token);
            switch (answer.Status)
            {
                case NousReadStatus.Ok when NousWalletParser.Parse(answer.Body!, now) is { } reading:
                    var changed = Wallet is null || reading.TotalUsd != Wallet.TotalUsd;
                    Wallet = reading;
                    var gauge = WalletGauge.Next(Gauge, reading.TotalUsd);
                    if (gauge != Gauge)
                    {
                        Gauge = gauge;
                        SaveGauge(gauge);
                    }

                    _schedule.OnSuccess(now, balanceChanged: changed);
                    SetStatus(NousStatus.SignedIn, force: true);
                    break;
                case NousReadStatus.Ok:
                    Log.Warn("nous", "Nous wallet not in a known format");
                    _schedule.OnFailure(now, TimeSpan.FromMinutes(5));
                    SetStatus(NousStatus.UnknownFormat);
                    break;
                case NousReadStatus.SignInRejected:
                    // An access token Nous no longer takes: one renewal, else the sign-in is over.
                    if (!await RenewAsync(now, force: true))
                    {
                        return;
                    }

                    _schedule.OnSuccess(now, balanceChanged: false);
                    break;
                default:
                    Log.Info("nous", "Nous wallet not read", new { status = answer.Status.ToString() });
                    _schedule.OnFailure(now, answer.RetryAfter);
                    SetStatus(NousStatus.Unavailable);
                    break;
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Log.Warn("nous", "Nous wallet read failed", new { error = ex.GetType().Name });
            _schedule.OnFailure(now);
            SetStatus(NousStatus.Unavailable);
        }
        finally
        {
            _walletBusy = false;
            ArmWallet();
        }
    }

    /// <summary>Renews the access token. The rotated refresh token is saved before anything else uses it.</summary>
    private async Task<bool> RenewAsync(DateTimeOffset now, bool force = false)
    {
        if (!force && _tokens!.ExpiresAt - now >= RefreshAhead)
        {
            return true;
        }

        var (status, tokens, retryAfter) = await _portal.RefreshAsync(_tokens!.RefreshToken, now, _shutdown.Token);
        switch (status)
        {
            case NousReadStatus.Ok:
                _store.Save(tokens!);
                _tokens = tokens;
                return true;
            case NousReadStatus.SignInRejected:
                Log.Info("nous", "Nous sign-in no longer accepted");
                _tokens = null;
                _store.Delete();
                Wallet = null;
                SetStatus(NousStatus.Expired, force: true);
                return false;
            default:
                _schedule.OnFailure(now, retryAfter);
                SetStatus(NousStatus.Unavailable);
                return false;
        }
    }

    private WalletGauge? LoadGauge()
    {
        try
        {
            if (!File.Exists(_gaugePath))
            {
                return null;
            }

            using var doc = JsonDocument.Parse(File.ReadAllText(_gaugePath));
            return new WalletGauge(doc.RootElement.GetProperty("full_usd").GetDecimal(), doc.RootElement.GetProperty("last_usd").GetDecimal());
        }
        catch (Exception ex)
        {
            Log.Debug("nous", "Wallet scale not read", new { error = ex.GetType().Name });
            return null;
        }
    }

    private void SaveGauge(WalletGauge gauge)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_gaugePath)!);
            var temp = _gaugePath + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(new Dictionary<string, decimal> { ["full_usd"] = gauge.FullUsd, ["last_usd"] = gauge.LastUsd }));
            File.Move(temp, _gaugePath, overwrite: true);
        }
        catch (Exception ex)
        {
            Log.Debug("nous", "Wallet scale not saved", new { error = ex.GetType().Name });
        }
    }

    // Hermes's token use ------------------------------------------------------------------------------------

    private void ArmUsage(TimeSpan delay)
    {
        _usageTimer.Stop();
        if (!_enabled || _disposed || _source == HermesUsageSource.Off)
        {
            return;
        }

        _usageTimer.Interval = delay;
        _usageTimer.Start();
    }

    private async Task ReadUsageAsync()
    {
        if (!_enabled || _usageBusy || _disposed)
        {
            return;
        }

        _usageBusy = true;
        var source = _source;
        try
        {
            var (usage, problem) = source switch
            {
                HermesUsageSource.Local => await Task.Run(ReadLocal),
                HermesUsageSource.Remote => await ReadRemoteAsync(),
                _ => (null, null),
            };
            if (source != _source)
            {
                return;
            }

            var previous = Usage?.LastActivity;
            if (usage is not null)
            {
                Usage = usage;
                if (usage.LastActivity is { } activity && (previous is null || activity > previous)
                    && DateTimeOffset.UtcNow - activity < NousPollSchedule.QuietAfter)
                {
                    // Hermes just worked: read the wallet every second for the next five minutes.
                    _schedule.Wake(DateTimeOffset.UtcNow);
                    ArmWallet();
                }
            }

            UsageProblem = problem;
            Changed?.Invoke();
        }
        catch (Exception ex)
        {
            Log.Warn("hermes", "Hermes usage read failed", new { error = ex.GetType().Name });
            UsageProblem = "Hermes usage could not be read";
            Changed?.Invoke();
        }
        finally
        {
            _usageBusy = false;
            ArmUsage(UsageInterval);
        }
    }

    private static (HermesUsage?, string?) ReadLocal()
    {
        var path = HermesStateDb.DefaultPath();
        if (!File.Exists(path))
        {
            return (null, "No Hermes install on this PC");
        }

        var usage = HermesStateDb.Read(path, TimeZoneInfo.Local, DateTimeOffset.UtcNow);
        return usage is null ? (null, "Hermes's database could not be read") : (usage, null);
    }

    /// <summary>Copies the server's export with Windows' OpenSSH (the user's key, the pinned host key, no prompts).</summary>
    private async Task<(HermesUsage?, string?)> ReadRemoteAsync()
    {
        if (_remoteTarget.Length == 0 || !File.Exists(_remoteKey))
        {
            return (null, "Set the server and key in Settings");
        }

        var scp = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "OpenSSH", "scp.exe");
        if (!File.Exists(scp))
        {
            return (null, "Windows OpenSSH is not installed");
        }

        var temp = _usageCache + ".part";
        var start = new ProcessStartInfo(scp)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
        };
        foreach (var arg in new[] { "-q", "-i", _remoteKey, "-o", "BatchMode=yes", "-o", "StrictHostKeyChecking=yes", "-o", "ConnectTimeout=10",
                     _remoteTarget + ":" + _remotePath, temp })
        {
            start.ArgumentList.Add(arg);
        }

        Directory.CreateDirectory(Path.GetDirectoryName(_usageCache)!);
        using var process = Process.Start(start)!;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(); } catch (InvalidOperationException) { }
            return (null, "The server did not answer");
        }

        if (process.ExitCode != 0 || !File.Exists(temp))
        {
            // The error text can name the host; only the exit code is logged.
            Log.Info("hermes", "Hermes export not copied", new { exit = process.ExitCode });
            return (null, "The server's export could not be copied");
        }

        File.Move(temp, _usageCache, overwrite: true);
        var usage = HermesUsageParser.Parse(await File.ReadAllTextAsync(_usageCache, _shutdown.Token), TimeZoneInfo.Local);
        return usage is null ? (null, "The server's export is not in a known format") : (usage, null);
    }

    private void SetStatus(NousStatus status, bool force = false)
    {
        if (status == Status && !force)
        {
            return;
        }

        if (status != Status)
        {
            Log.Info("nous", "Nous status", new { status = status.ToString() });
        }

        Status = status;
        Changed?.Invoke();
    }

    private static HttpClient CreateHttp()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd($"Claudex/{typeof(HermesService).Assembly.GetName().Version?.ToString(3) ?? "0"}");
        return http;
    }

    public void Dispose()
    {
        _disposed = true;
        _walletTimer.Stop();
        _usageTimer.Stop();
        _shutdown.Cancel();
        _shutdown.Dispose();
    }
}
