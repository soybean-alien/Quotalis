using System.Text.Json.Nodes;
using Quotalis.Core;
using Quotalis.Core.Plan;

namespace Quotalis;

/// <summary>
/// <c>Quotalis.exe --quotalis-calibrate ...</c> (see <see cref="PlanCalibration"/>), which writes only the plan usage
/// file the running island watches plus a one-line log, and <c>Quotalis.exe --quotalis-codex-stop-hook</c>
/// (see <see cref="CodexStopHook"/>), which writes only its trace. All run before any UI or single-instance setup.
/// </summary>
internal static class CalibrationCommand
{
    private static string LogFile => AppPaths.CalibrationLog;

    private static string CodexHookTrace => AppPaths.CodexHookTrace;

    public static bool IsCalibrate(string[] args) => LegacyNames.HasArgument(args, PlanCalibration.Argument);

    public static bool IsStopHook(string[] args) => args.Contains(LegacyClaudeStopHook.Argument, StringComparer.Ordinal);

    public static bool IsCodexStopHook(string[] args) => LegacyNames.HasArgument(args, CodexStopHook.Argument);

    public static bool IsHooks(string[] args) => LegacyNames.HasArgument(args, HookInstaller.Argument);

    /// <summary><c>--quotalis-hooks connect|repoint|disconnect</c> (the installer and uninstaller); exit 1 on failure.</summary>
    public static int Hooks(string[] args)
    {
        var action = args.SkipWhile(a => !LegacyNames.IsArgument(a, HookInstaller.Argument)).Skip(1).FirstOrDefault();
        if (Environment.ProcessPath is not { } exe || action is not ("connect" or "repoint" or "disconnect"))
        {
            Console.Error.WriteLine("usage: Quotalis.exe --quotalis-hooks connect|repoint|disconnect");
            return 1;
        }

        try
        {
            var claude = ClaudeStatusLineSetup.SettingsPath();
            var codex = HookInstaller.CodexHooksPath();
            switch (action)
            {
                case "connect":
                    HookInstaller.Connect(claude, codex, exe);
                    break;
                case "repoint":
                    HookInstaller.Repoint(claude, codex, exe);
                    break;
                default:
                    HookInstaller.Disconnect(claude, codex, exe);
                    break;
            }

            Console.WriteLine($"Hooks {action}ed for {exe}");
            return 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or InvalidOperationException)
        {
            Console.Error.WriteLine($"Hooks not {action}ed: {ex.Message}");
            return 1;
        }
    }

    public static int Calibrate(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        try
        {
            var now = DateTimeOffset.UtcNow;
            var usage = PlanCalibration.Parse(args, now);
            new PlanUsageStore(AppPaths.ClaudePlan).Save(usage);
            AppendLog(new JsonObject
            {
                ["at"] = now.ToString("o"),
                ["by"] = PlanCalibration.Value(args, "by") ?? "manual",
                ["source"] = PlanCalibration.Value(args, "source"),
                ["plan"] = PlanCalibration.Value(args, "plan"),
                ["five_hour"] = Window(usage.Current),
                ["weekly"] = Window(usage.Weekly),
            });
            Console.WriteLine($"Claude: 5h {Text(usage.Current)}, weekly {Text(usage.Weekly)}");
            Console.WriteLine("applied by Quotalis");
            return 0;
        }
        catch (FormatException ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"Plan usage not saved: {ex.Message}");
            return 2;
        }
    }

    /// <summary>
    /// <c>--quotalis-stop-hook</c>, the Claude Stop hook of versions before 0.5.14. It does nothing now (the installer
    /// removes its entries), so an entry left behind in Claude Code's settings never blocks a turn or costs a model call.
    /// </summary>
    public static int StopHook() => 0;

    public static int CodexStop()
    {
        string? reason = null;
        string why;
        var now = DateTimeOffset.UtcNow;
        var lastReport = LastReport();
        try
        {
            (reason, why) = CodexStopHook.Decide(StatusLineBridge.ReadInput(), lastReport, now, Environment.ProcessPath ?? "Quotalis.exe");
        }
        catch (Exception ex)
        {
            why = $"error: {ex.GetType().Name}"; // never break the session
        }

        try
        {
            Directory.CreateDirectory(AppPaths.Shared);
            File.WriteAllText(CodexHookTrace, new JsonObject
            {
                ["at"] = now.ToString("o"),
                ["blocked"] = reason is not null,
                ["why"] = why,
                ["reported_at"] = (reason is not null ? now : lastReport)?.ToString("o"),
            }.ToJsonString());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }

        if (reason is not null)
        {
            Console.OutputEncoding = System.Text.Encoding.UTF8;
            Console.WriteLine(CodexStopHook.Output(reason));
        }

        return 0;
    }

    private static DateTimeOffset? LastReport()
    {
        try
        {
            return File.Exists(CodexHookTrace)
                && JsonNode.Parse(File.ReadAllText(CodexHookTrace))?["reported_at"]?.GetValue<string>() is { } raw
                && DateTimeOffset.TryParse(raw, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal, out var at)
                ? at
                : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or InvalidOperationException)
        {
            return null;
        }
    }

    private static JsonObject? Window(PlanWindow? w) => w is null ? null : new JsonObject { ["percent"] = w.UsedPercent, ["resets_at"] = w.ResetsAt.ToString("o") };

    private static string Text(PlanWindow? w) =>
        w is null ? "--" : $"{w.UsedPercent.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture)}% (resets {w.ResetsAt.ToLocalTime():ddd MM-dd HH:mm})";

    private static void AppendLog(JsonObject row)
    {
        try
        {
            Directory.CreateDirectory(AppPaths.Shared);
            File.AppendAllText(LogFile, row.ToJsonString(new System.Text.Json.JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }) + "\n");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
