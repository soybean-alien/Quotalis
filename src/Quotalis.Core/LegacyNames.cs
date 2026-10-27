namespace Quotalis.Core;

/// <summary>
/// Quotalis was called Claudex until 0.7.0. Claude Code's <c>settings.json</c>, Codex's <c>hooks.json</c> and older
/// installers may still run the old command-line arguments, so every <c>--quotalis-…</c> argument also answers to its
/// <c>--claudex-…</c> name. The installer's hook step rewrites old entries to the new name.
/// </summary>
public static class LegacyNames
{
    public const string OldName = "Claudex";

    /// <summary>The pre-0.7 spelling of a <c>--quotalis-…</c> argument.</summary>
    public static string Old(string argument) => argument.Replace("--quotalis-", "--claudex-", StringComparison.Ordinal);

    /// <summary>True when <paramref name="args"/> holds the argument under its current or its old name.</summary>
    public static bool HasArgument(IEnumerable<string> args, string argument)
    {
        var old = Old(argument);
        return args.Any(a => a == argument || a == old);
    }

    /// <summary>True when <paramref name="arg"/> is the argument under its current or its old name.</summary>
    public static bool IsArgument(string arg, string argument) => arg == argument || arg == Old(argument);

    /// <summary>True when a command line contains the argument under its current or its old name.</summary>
    public static bool Mentions(string? command, string argument) =>
        command is not null && (command.Contains(argument, StringComparison.Ordinal) || command.Contains(Old(argument), StringComparison.Ordinal));

    /// <summary>Written into a new data folder once the old one has been moved into it.</summary>
    public const string MovedMarker = ".moved-from-claudex";

    /// <summary>
    /// The installed copy's move of an old data folder (see <see cref="MoveFolder"/>). A new folder without
    /// <see cref="MovedMarker"/> was made by a development build or a demo run before the installed copy first started,
    /// so it holds nothing real: it is set aside (renamed with a time stamp, never deleted) and the old folder moves in
    /// whole. Once marked, later calls only bring over what a locked file kept behind.
    /// </summary>
    public static bool AdoptFolder(string from, string to, DateTimeOffset now)
    {
        if (!Directory.Exists(from))
        {
            return true;
        }

        try
        {
            if (Directory.Exists(to) && !File.Exists(Path.Combine(to, MovedMarker)))
            {
                Directory.Move(to, $"{to}.before-{OldName.ToLowerInvariant()}-{now:yyyyMMdd-HHmmss}");
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;   // another process is doing it, or something is open there: the next launch tries again
        }

        var done = MoveFolder(from, to);
        try
        {
            if (Directory.Exists(to))
            {
                File.WriteAllText(Path.Combine(to, MovedMarker), string.Empty);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }

        return done;
    }

    /// <summary>
    /// Moves an old data folder's contents to <paramref name="to"/>. What already exists there wins (a hook may have
    /// written a new file first) and its old copy is left where it is, never deleted; the rest moves over, and the old
    /// folder is deleted once empty. A locked entry stays behind for a later call. True when <paramref name="from"/> is
    /// gone afterwards.
    /// </summary>
    public static bool MoveFolder(string from, string to)
    {
        try
        {
            if (!Directory.Exists(from))
            {
                return true;
            }

            if (!Directory.Exists(to))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(to))!);
                Directory.Move(from, to);
                return true;
            }

            foreach (var entry in Directory.EnumerateFileSystemEntries(from).ToList())
            {
                var target = Path.Combine(to, Path.GetFileName(entry));
                if (File.Exists(target) || Directory.Exists(target))
                {
                    continue;
                }

                try
                {
                    if (Directory.Exists(entry))
                    {
                        Directory.Move(entry, target);
                    }
                    else
                    {
                        File.Move(entry, target);
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Locked by a copy still running; a later launch moves it.
                }
            }

            if (Directory.EnumerateFileSystemEntries(from).Any())
            {
                return false;
            }

            Directory.Delete(from);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
