using Quotalis.Core;
using Quotalis.Core.Models;
using Quotalis.Core.Storage;
using Quotalis.Core.Usage;

namespace Quotalis;

/// <summary>
/// <c>Quotalis.exe --quotalis-import-codex-profile &lt;profile.json&gt;</c>: imports the Codex "Token activity" of a ChatGPT
/// profile (the JSON of <c>chatgpt.com/backend-api/profiles/&lt;name&gt;/page</c>) for days Quotalis has no local record of
/// (see <see cref="CodexProfileImport"/>). Safe to run again with a newer export: imported days are refreshed, measured
/// days are never touched. Restart the island afterwards to see the days in the graph.
/// </summary>
internal static class ImportCommand
{
    public const string Argument = "--quotalis-import-codex-profile";

    public static bool IsRequested(string[] args) => LegacyNames.HasArgument(args, Argument);

    public static int Run(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        var path = args.SkipWhile(a => !LegacyNames.IsArgument(a, Argument)).Skip(1).FirstOrDefault();
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            Console.Error.WriteLine($"usage: Quotalis.exe {Argument} <profile.json>");
            return 1;
        }

        try
        {
            var days = CodexProfileImport.Parse(File.ReadAllText(path));
            using var db = UsageDatabase.Open(AppPaths.Database);
            var local = db.GetDaily(DateOnly.MinValue, DateOnly.FromDateTime(DateTime.Today)).Where(d => d.Provider == AiProviderKind.Codex);
            var measured = local.Where(d => db.HasMeasuredDaily(d.Day, AiProviderKind.Codex)).ToList();
            var mix = TokenMix.FromLocal(measured);
            var result = CodexProfileImport.Import(db, days, mix, DateTimeOffset.UtcNow);
            Console.WriteLine($"Profile days: {days.Count}. Imported {result.Imported} ({result.Tokens:N0} tokens, estimated {Core.Formatting.DisplayFormat.Usd(result.EstimatedUsd)}); skipped {result.Skipped} from the first locally measured day on.");
            Console.WriteLine($"Split used: input {mix.Input:P1}, output {mix.Output:P1}, cached input {mix.CacheRead:P1}; ${mix.UsdPerMillion:0.####} per million tokens.");
            return 0;
        }
        catch (Exception ex) when (ex is FormatException or IOException or UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException)
        {
            Console.Error.WriteLine($"Import failed: {ex.Message}");
            return 2;
        }
    }
}
