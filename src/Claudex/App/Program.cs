using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace Claudex;

public static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        // Claude Code status line bridge: no UI, no single-instance check, done in milliseconds.
        if (StatusLineBridge.IsRequested(args))
        {
            return StatusLineBridge.Run();
        }

        // Plan usage from the Claude desktop app's usage card: the Stop hook, and the calibration it asks Claude to run;
        // and Codex's Stop hook, which reports that session's exact limits.
        if (CalibrationCommand.IsStopHook(args))
        {
            return CalibrationCommand.StopHook();
        }

        if (CalibrationCommand.IsCodexStopHook(args))
        {
            return CalibrationCommand.CodexStop();
        }

        // Codex history from a ChatGPT profile export, for days before Claudex tracked Codex locally.
        if (ImportCommand.IsRequested(args))
        {
            return ImportCommand.Run(args);
        }

        // Installer and uninstaller: point the Claude Code and Codex hooks at this copy, or remove them.
        if (CalibrationCommand.IsHooks(args))
        {
            return CalibrationCommand.Hooks(args);
        }

        if (CalibrationCommand.IsCalibrate(args))
        {
            return CalibrationCommand.Calibrate(args);
        }

        // Awake backstop (a scheduled task): undo an ended session's lid change if the island is not running.
        if (AwakeRestore.IsRequested(args))
        {
            return AwakeRestore.Run();
        }

        // Hardware support check: prints what the System module can read, then exits.
        if (SystemReport.IsRequested(args))
        {
            return SystemReport.Run(args);
        }

        var options = AppOptions.Parse(args);
        if (options.SnapshotCulture is { Length: > 0 } cultureName)
        {
            // Documentation renders only: one number and date format whatever the machine uses.
            var culture = System.Globalization.CultureInfo.GetCultureInfo(cultureName);
            System.Globalization.CultureInfo.DefaultThreadCurrentCulture = culture;
            System.Globalization.CultureInfo.DefaultThreadCurrentUICulture = culture;
            System.Globalization.CultureInfo.CurrentCulture = culture;
            System.Globalization.CultureInfo.CurrentUICulture = culture;
        }

        // One island per user session. A second launch (Start menu, installer "Launch") shows the
        // running island instead. Demo mode uses its own instance name so it can run side by side, and
        // so does the Home Cloud upload debug run, next to the island the user has running.
        var name = options.HomeCloudUploadPath is not null ? (options.IsDemo ? "Claudex.HomeCloudUpload.Demo" : "Claudex.HomeCloudUpload")
            : options.SnapshotDirectory is not null ? "Claudex.Snapshot"
            : options.IsDemo ? "Claudex.Demo" : "Claudex";
        using var mutex = new Mutex(true, $@"Local\{name}.Instance", out var isFirst);
        using var showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, $@"Local\{name}.Show");
        if (!isFirst)
        {
            if (options.HomeCloudUploadPath is not null)
            {
                return 3; // another upload debug run is still going
            }

            showSignal.Set();
            return 0;
        }

        WinRT.ComWrappersSupport.InitializeComWrappers();
        Application.Start(_ =>
        {
            var dispatcher = DispatcherQueue.GetForCurrentThread();
            SynchronizationContext.SetSynchronizationContext(new DispatcherQueueSynchronizationContext(dispatcher));
            var app = new App(options);
            ThreadPool.RegisterWaitForSingleObject(showSignal, (_, _) => dispatcher.TryEnqueue(() => app.OnSecondInstance()), null, Timeout.Infinite, executeOnlyOnce: false);
        });

        GC.KeepAlive(mutex);
        return ExitCode;
    }

    /// <summary>The process exit code; only the Home Cloud upload debug run sets it.</summary>
    internal static int ExitCode { get; set; }
}
