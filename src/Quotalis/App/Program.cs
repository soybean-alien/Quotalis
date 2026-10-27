using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace Quotalis;

public static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        // Before anything reads or writes data: the installed copy brings over the folders of Claudex, this app's name
        // before 0.7.0. A development build leaves them alone, so it can't take them from an installed Claudex.
        if (AppUpdater.IsInstalledCopy)
        {
            AppPaths.MoveLegacyFolders();
        }

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

        // Codex history from a ChatGPT profile export, for days before Quotalis tracked Codex locally.
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

        // The "Quotalis Awake Restore" task of versions before 0.7 may still be scheduled: Awake is gone, so its call
        // must not open the island.
        if (args.Any(a => a.Equals("--awake-restore", StringComparison.OrdinalIgnoreCase)))
        {
            return 0;
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
        // running island instead. Demo mode and snapshot runs use their own instance names, so they can run side by side.
        var name = options.SnapshotDirectory is not null ? "Quotalis.Snapshot"
            : options.IsDemo ? "Quotalis.Demo" : "Quotalis";
        using var mutex = new Mutex(true, $@"Local\{name}.Instance", out var isFirst);
        using var showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, $@"Local\{name}.Show");
        if (!isFirst)
        {
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
        return 0;
    }
}
