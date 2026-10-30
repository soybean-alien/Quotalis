using System.Diagnostics;
using Quotalis.Core.Diagnostics;
using Quotalis.Core.Plan;

namespace Quotalis.Linux;

/// <summary><c>quotalis setup</c>: Claude Code's status line, the user service and the bar module. Can be undone.</summary>
internal static class Setup
{
    private const string ServiceName = "quotalis.service";

    private static string ServicePath => Path.Combine(AppPaths.UserConfig, "systemd", "user", ServiceName);

    private static string WaybarFolder => Path.Combine(AppPaths.UserConfig, "waybar");

    private static string OmarchyShellConfig => Path.Combine(AppPaths.UserConfig, "omarchy", "shell.json");

    /// <summary>Omarchy 4 replaced Waybar with its own Quickshell bar, which this command talks to.</summary>
    private static bool HasOmarchyBar => ProcessRunner.FindExecutable("omarchy-shell") is not null;

    public static int Install(string executable)
    {
        ConnectStatusLine(executable);
        InstallService(executable);
        if (HasOmarchyBar)
        {
            InstallOmarchyBar(executable);
        }
        else
        {
            InstallWaybar(executable);
        }

        Console.WriteLine();
        Console.WriteLine("Done. Open the screen with: quotalis");
        return 0;
    }

    public static int Remove()
    {
        ClaudeStatusLineSetup.Disconnect(ClaudeStatusLineSetup.SettingsPath());
        Console.WriteLine("Claude Code's status line: disconnected");
        Run("systemctl", "--user", "disable", "--now", ServiceName);
        if (File.Exists(ServicePath))
        {
            File.Delete(ServicePath);
        }

        Run("systemctl", "--user", "daemon-reload");
        Console.WriteLine("User service: removed");

        RemoveOmarchyBar();
        var config = FindWaybarConfig();
        if (config is not null)
        {
            var current = File.ReadAllText(config);
            var updated = WaybarConfig.Remove(current);
            if (updated != current)
            {
                Backup(config);
                File.WriteAllText(config, updated);
                Console.WriteLine("Waybar module: removed");
            }
        }

        var style = Path.Combine(WaybarFolder, "style.css");
        if (File.Exists(style))
        {
            var css = File.ReadAllText(style);
            var styled = WaybarConfig.RemoveStyle(css);
            if (styled != css)
            {
                Backup(style);
                File.WriteAllText(style, styled);
            }
        }

        ReloadWaybar();
        Console.WriteLine("Your usage history stays in " + AppPaths.Root);
        return 0;
    }

    /// <summary>Opens the screen in a terminal window, for the bar's click.</summary>
    public static int Open(string executable)
    {
        foreach (var (terminal, args) in Terminals(executable))
        {
            if (ProcessRunner.FindExecutable(terminal) is not { } path)
            {
                continue;
            }

            var start = new ProcessStartInfo(path) { UseShellExecute = false };
            foreach (var arg in args)
            {
                start.ArgumentList.Add(arg);
            }

            Process.Start(start)?.Dispose();
            return 0;
        }

        Console.Error.WriteLine("No terminal found. Set $TERMINAL, or run quotalis in one.");
        return 1;
    }

    private static IEnumerable<(string Terminal, string[] Args)> Terminals(string executable)
    {
        yield return ("xdg-terminal-exec", ["--app-id=TUI.float", executable, "--fit"]);
        if (Environment.GetEnvironmentVariable("TERMINAL") is { Length: > 0 } own)
        {
            yield return (own, ["-e", executable, "--fit"]);
        }

        yield return ("alacritty", ["--class", "TUI.float", "-e", executable, "--fit"]);
        yield return ("ghostty", ["--class=TUI.float", "-e", executable, "--fit"]);
        yield return ("kitty", ["--class", "TUI.float", executable, "--fit"]);
        yield return ("foot", ["--app-id=TUI.float", executable, "--fit"]);
    }

    private static void ConnectStatusLine(string executable)
    {
        var result = ClaudeStatusLineSetup.Connect(ClaudeStatusLineSetup.SettingsPath(), executable);
        Console.WriteLine(result switch
        {
            PlanConnection.Connected => "Claude Code's status line: connected",
            PlanConnection.OtherStatusLine => "Claude Code's status line: left alone, another tool uses it, so Claude's plan bars stay empty",
            _ => "Claude Code's status line: could not be connected",
        });
    }

    private static void InstallService(string executable)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(ServicePath)!);
        File.WriteAllText(ServicePath, ServiceUnit(executable));
        if (ProcessRunner.FindExecutable("systemctl") is null)
        {
            Console.WriteLine("User service: systemctl not found. Start it yourself with: quotalis service");
            return;
        }

        var reloaded = Run("systemctl", "--user", "daemon-reload");
        var started = reloaded && Run("systemctl", "--user", "enable", "--now", ServiceName);
        Console.WriteLine(started ? "User service: running, and starts at login" : "User service: could not be started, see systemctl --user status " + ServiceName);
    }

    internal static string ServiceUnit(string executable) => $"""
        [Unit]
        Description=Quotalis AI usage readings
        After=default.target

        [Service]
        ExecStart="{executable}" service
        Restart=on-failure
        RestartSec=10

        [Install]
        WantedBy=default.target

        """;

    private static void InstallOmarchyBar(string executable)
    {
        var path = OmarchyShellConfig;
        var defaults = Path.Combine(Environment.GetEnvironmentVariable("OMARCHY_PATH") ?? "/usr/share/omarchy", "config", "omarchy", "shell.json");
        var source = File.Exists(path) ? path : defaults;
        if (!File.Exists(source))
        {
            Console.WriteLine("Omarchy bar: no shell.json found, so nothing changed");
            return;
        }

        var current = File.ReadAllText(source);
        var updated = OmarchyBarConfig.Add(current, executable);
        if (updated is null)
        {
            Console.WriteLine("Omarchy bar: couldn't read the bar layout in " + source + ", so nothing changed");
            return;
        }

        if (updated != current || source != path)
        {
            if (File.Exists(path))
            {
                Console.WriteLine("Omarchy bar config backed up to " + Backup(path));
            }

            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, updated);
        }

        Run("omarchy-shell", "shell", "reloadConfig");
        Console.WriteLine("Omarchy bar: module added");
    }

    private static void RemoveOmarchyBar()
    {
        var path = OmarchyShellConfig;
        if (!File.Exists(path))
        {
            return;
        }

        var current = File.ReadAllText(path);
        var updated = OmarchyBarConfig.Remove(current);
        if (updated is null || updated == current)
        {
            return;
        }

        Backup(path);
        File.WriteAllText(path, updated);
        Run("omarchy-shell", "shell", "reloadConfig");
        Console.WriteLine("Omarchy bar: module removed");
    }

    private static void InstallWaybar(string executable)
    {
        var config = FindWaybarConfig();
        if (config is null)
        {
            Console.WriteLine("Waybar module: no config found in " + WaybarFolder + ". Add this module yourself:");
            Console.WriteLine(WaybarConfig.Add("{ \"modules-right\": [] }", executable));
            return;
        }

        var current = File.ReadAllText(config);
        var updated = WaybarConfig.Add(current, executable);
        if (updated is null)
        {
            Console.WriteLine("Waybar module: couldn't find modules-right in " + config + ", so nothing changed");
            return;
        }

        if (updated != current)
        {
            Console.WriteLine("Waybar config backed up to " + Backup(config));
            File.WriteAllText(config, updated);
        }

        var style = Path.Combine(WaybarFolder, "style.css");
        if (File.Exists(style))
        {
            var css = File.ReadAllText(style);
            var styled = WaybarConfig.AddStyle(css);
            if (styled != css)
            {
                Backup(style);
                File.WriteAllText(style, styled);
            }
        }

        ReloadWaybar();
        Console.WriteLine("Waybar module: added");
    }

    private static string? FindWaybarConfig() =>
        new[] { "config.jsonc", "config", "config.json" }.Select(n => Path.Combine(WaybarFolder, n)).FirstOrDefault(File.Exists);

    private static string Backup(string path)
    {
        var copy = path + ".quotalis-backup-" + DateTime.Now.ToString("yyyyMMddHHmmss");
        File.Copy(path, copy);
        return copy;
    }

    /// <summary>SIGUSR2 tells Waybar to reload its config and style.</summary>
    private static void ReloadWaybar()
    {
        if (ProcessRunner.FindExecutable("pkill") is not null)
        {
            Run("pkill", "-SIGUSR2", "-x", "waybar");
        }
    }

    private static bool Run(string file, params string[] args)
    {
        try
        {
            var start = new ProcessStartInfo(file) { UseShellExecute = false, RedirectStandardError = true, RedirectStandardOutput = true };
            foreach (var arg in args)
            {
                start.ArgumentList.Add(arg);
            }

            using var process = Process.Start(start)!;
            process.WaitForExit(15000);
            return process.HasExited && process.ExitCode == 0;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return false;
        }
    }
}
