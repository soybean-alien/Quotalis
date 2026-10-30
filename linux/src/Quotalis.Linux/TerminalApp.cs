using System.Runtime.InteropServices;

namespace Quotalis.Linux;

/// <summary>The full-screen view. Reads the published state; closing it never affects the service.</summary>
internal static class TerminalApp
{
    private const string EnterScreen = "\x1b[?1049h\x1b[?25l";
    private const string LeaveScreen = "\x1b[?25h\x1b[?1049l";

    // Columns around the text in a fitted window. A cell is about twice as tall as it is wide, so two columns look like the one blank row above and below.
    private const int Margin = 2;

    public static int Run(Func<AppState?> read, bool color, bool fit = false)
    {
        if (Console.IsOutputRedirected || Console.IsInputRedirected)
        {
            PrintOnce(read(), ScreenPage.Overview, color);
            return 0;
        }

        var screen = new Screen(color);
        var page = ScreenPage.Overview;
        var restored = 0;
        void Restore()
        {
            if (Interlocked.Exchange(ref restored, 1) == 0)
            {
                Console.Write(LeaveScreen);
            }
        }

        using var hangup = PosixSignalRegistration.Create(PosixSignal.SIGTERM, _ => Restore());
        Console.CancelKeyPress += (_, _) => Restore();
        Console.Write(EnterScreen);
        try
        {
            var window = fit ? WindowFit.ForThisWindow() : null;
            var state = read();
            var lastRead = DateTimeOffset.UtcNow;
            var lastDraw = DateTimeOffset.MinValue;
            var dirty = true;
            while (true)
            {
                var now = DateTimeOffset.UtcNow;
                if (now - lastRead >= TimeSpan.FromSeconds(2))
                {
                    state = read() ?? state;
                    lastRead = now;
                }

                if (dirty || now - lastDraw >= TimeSpan.FromSeconds(1))
                {
                    var lines = screen.Render(state, page, now, Width());
                    window?.Apply(lines.Max(line => Screen.Plain(line).Length) + Margin * 2 - 1, lines.Count + 2);
                    Draw(lines, window is not null);
                    lastDraw = now;
                    dirty = false;
                }

                if (!Console.KeyAvailable)
                {
                    Thread.Sleep(50);
                    continue;
                }

                switch (char.ToLowerInvariant(Console.ReadKey(intercept: true).KeyChar))
                {
                    case 'q':
                    case '\x1b':
                        return 0;
                    case 'r':
                        state = read() ?? state;
                        lastRead = now;
                        dirty = true;
                        break;
                    case 'g':
                        page = ScreenPage.Git;
                        dirty = true;
                        break;
                    case 'h':
                        page = ScreenPage.History;
                        dirty = true;
                        break;
                    case 'u':
                        page = ScreenPage.Overview;
                        dirty = true;
                        break;
                }
            }
        }
        finally
        {
            Restore();
        }
    }

    public static void PrintOnce(AppState? state, ScreenPage page, bool color)
    {
        foreach (var line in new Screen(color).Render(state, page, DateTimeOffset.UtcNow, Width()))
        {
            Console.WriteLine(line);
        }
    }

    private static void Draw(IReadOnlyList<string> lines, bool margin)
    {
        // A fitted window's last row is blank, and moving past it would scroll the frame.
        var frame = new System.Text.StringBuilder("\x1b[H");
        var left = margin ? new string(' ', Margin - 1) : string.Empty;
        if (margin)
        {
            frame.Append("\x1b[K\r\n");
        }

        for (var i = 0; i < lines.Count; i++)
        {
            frame.Append(left).Append(lines[i]).Append("\x1b[K");
            if (i < lines.Count - 1 || !margin)
            {
                frame.Append("\r\n");
            }
        }

        frame.Append(margin ? "\r\n\x1b[K" : "\x1b[J");
        Console.Write(frame.ToString());
    }

    private static int Width()
    {
        try
        {
            return Console.IsOutputRedirected ? Screen.MaxWidth : Console.WindowWidth;
        }
        catch (IOException)
        {
            return Screen.MaxWidth;
        }
    }
}
