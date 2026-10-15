namespace LinkbarDesktops;

internal static class Program
{
    public const string ExitEventName = @"Local\LinkbarDesktops.Exit";

    [STAThread]
    private static void Main(string[] args)
    {
        if (args.Any(a => a.Equals("--exit", StringComparison.OrdinalIgnoreCase)))
        {
            // Ask a running instance to close its bars and quit.
            if (EventWaitHandle.TryOpenExisting(ExitEventName, out var exitEvent))
                using (exitEvent) exitEvent.Set();
            return;
        }

        using var mutex = new Mutex(true, @"Local\LinkbarDesktops.SingleInstance", out bool isFirstInstance);
        if (!isFirstInstance)
        {
            MessageBox.Show("Linkbar Desktops is already running (see the system tray).", "Linkbar Desktops",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        ApplicationConfiguration.Initialize();

        var settings = AppSettings.Load();
        var linkbarExe = settings.ResolveLinkbarExe();
        if (linkbarExe == null)
        {
            MessageBox.Show(
                "Couldn't find Linkbar.\n\n" +
                "Put the Linkbar files in a \"Linkbar\" folder next to LinkbarDesktops.exe, or set \"LinkbarPath\" in:\n" +
                AppSettings.ConfigPath,
                "Linkbar Desktops", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        Log.Write($"Starting with {linkbarExe}");
        Application.Run(new TrayContext(linkbarExe));
    }
}
