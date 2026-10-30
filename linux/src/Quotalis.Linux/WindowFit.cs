using System.Diagnostics;
using System.Text.Json;

namespace Quotalis.Linux;

/// <summary>
/// Shrinks the bar click's floating window to the text inside it, on Hyprland with Lua dispatchers (0.55 and later).
/// A terminal doesn't say how big its cells are in window units, so the smallest size that still holds the text is
/// found by trying sizes. Anywhere else this does nothing, and the window keeps the size the desktop gave it.
/// </summary>
internal sealed class WindowFit
{
    private const string FloatingAppId = "TUI.float";
    private static readonly TimeSpan Settle = TimeSpan.FromMilliseconds(40);

    private readonly string _address;
    private int _columns;
    private int _rows;
    private bool _failed;

    private WindowFit(string address) => _address = address;

    /// <summary>Null unless this is the floating window the bar's click opens, so an ordinary terminal is never resized.</summary>
    public static WindowFit? ForThisWindow()
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("HYPRLAND_INSTANCE_SIGNATURE"))
            || Hyprctl("-j", "activewindow") is not { } json)
        {
            return null;
        }

        try
        {
            using var window = JsonDocument.Parse(json);
            var root = window.RootElement;
            return root.GetProperty("class").GetString() == FloatingAppId && root.GetProperty("floating").GetBoolean()
                ? new WindowFit(root.GetProperty("address").GetString()!)
                : null;
        }
        catch (Exception exception) when (exception is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            return null;
        }
    }

    public void Apply(int columns, int rows)
    {
        if (_failed || (columns == _columns && rows == _rows) || Size() is not { } size)
        {
            return;
        }

        _columns = columns;
        _rows = rows;
        var cellWidth = size.Width / (double)Console.WindowWidth;
        var cellHeight = size.Height / (double)Console.WindowHeight;
        var (lowWidth, highWidth) = ((int)((columns - 1) * cellWidth), (int)Math.Ceiling((columns + 1) * cellWidth));
        var (lowHeight, highHeight) = ((int)((rows - 1) * cellHeight), (int)Math.Ceiling((rows + 1) * cellHeight));

        // A terminal's own padding can put the answer above the first guess.
        for (var tries = 0; tries < 4 && TryFits(highWidth, highHeight, columns, rows) is not (true, true); tries++)
        {
            highWidth += (int)Math.Ceiling(cellWidth);
            highHeight += (int)Math.Ceiling(cellHeight);
        }

        while (!_failed && (highWidth - lowWidth > 1 || highHeight - lowHeight > 1))
        {
            var (width, height) = ((lowWidth + highWidth) / 2, (lowHeight + highHeight) / 2);
            var (widthFits, heightFits) = TryFits(width, height, columns, rows);
            if (highWidth - lowWidth > 1)
            {
                if (widthFits) highWidth = width;
                else lowWidth = width;
            }

            if (highHeight - lowHeight > 1)
            {
                if (heightFits) highHeight = height;
                else lowHeight = height;
            }
        }

        Dispatch($"hl.dsp.window.resize({{ x = {highWidth}, y = {highHeight}, window = \"address:{_address}\" }})");
        Dispatch($"hl.dsp.window.center({{ window = \"address:{_address}\" }})");
        Thread.Sleep(Settle);
    }

    private (bool Width, bool Height) TryFits(int width, int height, int columns, int rows)
    {
        Dispatch($"hl.dsp.window.resize({{ x = {width}, y = {height}, window = \"address:{_address}\" }})");
        Thread.Sleep(Settle);
        return (Console.WindowWidth >= columns, Console.WindowHeight >= rows);
    }

    private (int Width, int Height)? Size()
    {
        if (Hyprctl("-j", "clients") is not { } json)
        {
            _failed = true;
            return null;
        }

        try
        {
            using var clients = JsonDocument.Parse(json);
            foreach (var client in clients.RootElement.EnumerateArray())
            {
                if (client.GetProperty("address").GetString() == _address)
                {
                    var size = client.GetProperty("size");
                    return (size[0].GetInt32(), size[1].GetInt32());
                }
            }
        }
        catch (Exception exception) when (exception is JsonException or KeyNotFoundException or InvalidOperationException)
        {
        }

        _failed = true;
        return null;
    }

    private void Dispatch(string call)
    {
        if (!_failed && Hyprctl("dispatch", call)?.Trim() != "ok")
        {
            _failed = true;
        }
    }

    private static string? Hyprctl(params string[] arguments)
    {
        try
        {
            var start = new ProcessStartInfo("hyprctl") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var argument in arguments)
            {
                start.ArgumentList.Add(argument);
            }

            using var process = Process.Start(start);
            if (process is null)
            {
                return null;
            }

            var errors = process.StandardError.ReadToEndAsync();
            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit();
            _ = errors.Result;
            return process.ExitCode == 0 ? output : null;
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return null;
        }
    }
}
