using System.Runtime.InteropServices;
using System.Text;
using Quotalis.Core.Plan;
using Quotalis.Core.Settings;

namespace Quotalis.Linux;

internal static class Program
{
    private const string Help = """
        Quotalis, AI usage limits in your terminal and bar.

          quotalis                  Open the screen ([g] git, [h] history, [q] quit)
          quotalis setup            Connect Claude Code, start the service, add the bar module
          quotalis setup --remove   Undo all of that (your usage history stays)
          quotalis service          Run the background service by hand
          quotalis bar              One line for Omarchy's bar
          quotalis waybar           The Waybar module's output
          quotalis print [page]     Print a page once: usage, git or history
          quotalis signin chatgpt   Read Codex's plan limits from your ChatGPT account
          quotalis signout chatgpt  Forget that sign-in
          quotalis config           Show where settings are kept
          quotalis version          Show the version

        Add --demo to the screen, print or waybar to see made-up sample data.
        Colours follow the terminal's theme. NO_COLOR turns them off.
        """;

    private static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        var demo = args.Contains("--demo");
        var fit = args.Contains("--fit");
        var words = args.Where(a => a is not ("--demo" or "--fit")).ToArray();
        var color = string.IsNullOrEmpty(Environment.GetEnvironmentVariable("NO_COLOR"));

        // Claude Code's status line: no output, in milliseconds, and it can never fail Claude Code.
        if (Quotalis.Core.LegacyNames.HasArgument(args, ClaudeStatusLineSetup.Argument))
        {
            return RunStatusLine();
        }

        var command = words.FirstOrDefault() ?? "screen";
        if (command is "help" or "--help" or "-h")
        {
            Console.WriteLine(Help);
            return 0;
        }

        if (command is "version" or "--version")
        {
            Console.WriteLine("Quotalis for Linux " + typeof(Program).Assembly.GetName().Version?.ToString(3));
            return 0;
        }

        Func<AppState?> read = demo ? () => StateBuilder.Demo(DateTimeOffset.UtcNow) : () => AppStateFile.Read(AppPaths.State);
        if (!demo && !AppPaths.SafeForRealData)
        {
            Console.Error.WriteLine("On Windows, real data needs QUOTALIS_HOME set, so this can't touch the Windows app's folders. Add --demo for sample data.");
            return 2;
        }

        var executable = Environment.ProcessPath ?? "quotalis";
        switch (command)
        {
            case "screen":
                return TerminalApp.Run(CachedDemo(read, demo), color, fit);
            case "print":
                TerminalApp.PrintOnce(read(), ParsePage(words.ElementAtOrDefault(1)), color);
                return 0;
            case "bar":
                Console.WriteLine(WaybarOutput.Render(read(), DateTimeOffset.UtcNow, omarchy: true));
                return 0;
            case "waybar":
                return await RunWaybarAsync(CachedDemo(read, demo), words.Contains("--once"));
            case "service":
                return await RunServiceAsync();
            case "setup":
                return words.Contains("--remove") ? Setup.Remove() : Setup.Install(executable);
            case "open":
                return Setup.Open(executable);
            case "signin" when words.ElementAtOrDefault(1) == "chatgpt":
                return await SignInAsync();
            case "signout" when words.ElementAtOrDefault(1) == "chatgpt":
                using (var client = new CodexAccountClient())
                {
                    await client.SignOutAsync();
                }

                Console.WriteLine("Signed out.");
                return 0;
            case "config":
                Console.WriteLine("Settings: " + new SettingsStore(AppPaths.Settings).FilePath);
                Console.WriteLine("Data:     " + AppPaths.Root);
                Console.WriteLine("Shared:   " + AppPaths.Shared);
                return 0;
            default:
                Console.Error.WriteLine(Help);
                return 2;
        }
    }

    /// <summary>Sample data is built once for the run; real state is just a small file read each time.</summary>
    private static Func<AppState?> CachedDemo(Func<AppState?> read, bool demo)
    {
        if (!demo)
        {
            return read;
        }

        var state = read();
        return () => state;
    }

    private static ScreenPage ParsePage(string? name) => name switch
    {
        "git" => ScreenPage.Git,
        "history" => ScreenPage.History,
        _ => ScreenPage.Overview,
    };

    private static int RunStatusLine()
    {
        try
        {
            if (ClaudeStatusLine.Extract(Console.In.ReadToEnd(), DateTimeOffset.UtcNow) is { } usage)
            {
                new PlanUsageStore(AppPaths.ClaudePlan).Save(usage);
            }
        }
        catch
        {
            // Never fail Claude Code's status line: no output, no error code.
        }

        return 0;
    }

    private static async Task<int> RunServiceAsync()
    {
        Directory.CreateDirectory(AppPaths.Root);
        FileStream serviceLock;
        try
        {
            serviceLock = new FileStream(AppPaths.ServiceLock, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException)
        {
            Console.Error.WriteLine("The Quotalis service is already running.");
            return 1;
        }

        using (serviceLock)
        using (var stop = new CancellationTokenSource())
        using (var term = PosixSignalRegistration.Create(PosixSignal.SIGTERM, context => { context.Cancel = true; stop.Cancel(); }))
        using (var interrupt = PosixSignalRegistration.Create(PosixSignal.SIGINT, context => { context.Cancel = true; stop.Cancel(); }))
        using (var service = new UsageService())
        {
            await service.RunAsync(stop.Token);
        }

        return 0;
    }

    /// <summary>Waybar reads one JSON line per change. The countdowns move, so the line is rebuilt every few seconds.</summary>
    private static async Task<int> RunWaybarAsync(Func<AppState?> read, bool once)
    {
        string? last = null;
        while (true)
        {
            var line = WaybarOutput.Render(read(), DateTimeOffset.UtcNow);
            if (line != last)
            {
                Console.WriteLine(line);
                last = line;
            }

            if (once)
            {
                return 0;
            }

            await Task.Delay(TimeSpan.FromSeconds(5));
        }
    }

    private static async Task<int> SignInAsync()
    {
        if (!CodexAccountClient.IsAvailable)
        {
            Console.Error.WriteLine("Codex is required for this sign-in. Install the Codex CLI first.");
            return 1;
        }

        using var client = new CodexAccountClient();
        var url = await client.StartSignInAsync();
        Console.WriteLine("Open this address in your browser and approve the sign-in:");
        Console.WriteLine(url);
        for (var i = 0; i < 150; i++)
        {
            await Task.Delay(TimeSpan.FromSeconds(2));
            if (await client.IsSignedInAsync())
            {
                Console.WriteLine("Signed in. Codex's plan limits appear within a few minutes.");
                return 0;
            }
        }

        Console.Error.WriteLine("The sign-in wasn't completed.");
        return 1;
    }
}
