using System.Diagnostics;
using System.Text.Json;
using Quotalis.Core.Diagnostics;

namespace Quotalis.Linux;

/// <summary>
/// Reads the user's Work/Codex allowance through the locally installed Codex app-server protocol. Codex owns the
/// ChatGPT sign-in and its credentials in Quotalis's separate CODEX_HOME; Quotalis handles only the OAuth URL and
/// usage percentages. This never changes the account used by the Codex CLI.
/// </summary>
internal sealed class CodexAccountClient : IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private Process? _process;
    private int _nextId;
    private bool _disposed;

    public static bool IsAvailable => FindExecutable() is not null;

    public async Task<bool> IsSignedInAsync(CancellationToken cancellationToken = default)
    {
        var result = await RequestAsync("account/read", new { }, cancellationToken);
        return result.TryGetProperty("account", out var account) && account.ValueKind == JsonValueKind.Object
            && account.TryGetProperty("type", out var type) && type.GetString() == "chatgpt";
    }

    public async Task<string> StartSignInAsync(CancellationToken cancellationToken = default)
    {
        var result = await RequestAsync("account/login/start", new { type = "chatgpt", appBrand = "chatgpt" }, cancellationToken);
        var url = result.GetProperty("authUrl").GetString();
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps
            || !(uri.Host.Equals("auth.openai.com", StringComparison.OrdinalIgnoreCase)
                || uri.Host.Equals("chatgpt.com", StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException("Codex returned an unexpected sign-in address.");
        }

        return url!;
    }

    public async Task<string> ReadLimitsAsync(CancellationToken cancellationToken = default)
    {
        var result = await RequestAsync("account/rateLimits/read", new { }, cancellationToken);
        return result.GetRawText();
    }

    public Task SignOutAsync(CancellationToken cancellationToken = default) =>
        RequestAsync("account/logout", new { }, cancellationToken);

    private async Task<JsonElement> RequestAsync(string method, object parameters, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await EnsureStartedAsync(cancellationToken);
            return await SendAsync(method, parameters, cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task EnsureStartedAsync(CancellationToken cancellationToken)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(CodexAccountClient));
        if (_process is { HasExited: false }) return;
        _process?.Dispose();
        var executable = FindExecutable() ?? throw new FileNotFoundException("Codex is not installed.");
        Directory.CreateDirectory(AppPaths.ChatGptProfile);
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        // Otherwise Codex downloads its plugin catalogue into this profile, about 95 MB this program never uses.
        start.ArgumentList.Add("-c");
        start.ArgumentList.Add("features.plugins=false");
        start.ArgumentList.Add("app-server");
        start.ArgumentList.Add("--stdio");
        start.Environment["CODEX_HOME"] = AppPaths.ChatGptProfile;
        _process = Process.Start(start) ?? throw new InvalidOperationException("Codex app-server did not start.");
        var process = _process;
        // Drain diagnostics without saving them: Codex may include account information in an error.
        _ = Task.Run(async () =>
        {
            try { while (await process.StandardError.ReadLineAsync() is not null) { } }
            catch (Exception) { }
        });
        try
        {
            var version = typeof(CodexAccountClient).Assembly.GetName().Version?.ToString(3) ?? "0.1.0";
            await SendAsync("initialize", new { clientInfo = new { name = "Quotalis", version } }, cancellationToken);
            await process.StandardInput.WriteLineAsync("{\"method\":\"initialized\",\"params\":{}}");
            await process.StandardInput.FlushAsync(cancellationToken);
        }
        catch
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (Exception) { }
            process.Dispose();
            _process = null;
            throw;
        }
    }

    private async Task<JsonElement> SendAsync(string method, object parameters, CancellationToken cancellationToken)
    {
        var process = _process ?? throw new InvalidOperationException("Codex app-server is unavailable.");
        var id = ++_nextId;
        var request = JsonSerializer.Serialize(new { id, method, @params = parameters });
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(25));
        await process.StandardInput.WriteLineAsync(request.AsMemory(), timeout.Token);
        await process.StandardInput.FlushAsync(timeout.Token);
        while (await process.StandardOutput.ReadLineAsync(timeout.Token) is { } line)
        {
            using var response = JsonDocument.Parse(line);
            var root = response.RootElement;
            if (!root.TryGetProperty("id", out var responseId) || !responseId.TryGetInt32(out var number) || number != id)
            {
                continue; // notifications, including account/login/completed
            }

            if (root.TryGetProperty("error", out _)) throw new InvalidOperationException($"Codex could not complete {method}.");
            return root.GetProperty("result").Clone();
        }

        throw new IOException("Codex app-server closed before replying.");
    }

    private static string? FindExecutable()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return ProcessRunner.FindExecutable("codex", Path.Combine(home, ".local", "bin", "codex"), Path.Combine(home, ".npm-global", "bin", "codex"), "/usr/local/bin/codex", "/usr/bin/codex");
    }

    public void Dispose()
    {
        _disposed = true;
        try
        {
            if (_process is { HasExited: false } process) process.Kill(entireProcessTree: true);
        }
        catch (Exception) { }
        _process?.Dispose();
    }
}
