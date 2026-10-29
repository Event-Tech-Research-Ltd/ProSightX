namespace ProSight;

static class Program
{
    [STAThread]
    static int Main(string[] args)
    {
        if (args.Any(a => a.Equals("--selftest", StringComparison.OrdinalIgnoreCase)))
        {
            ApplicationConfiguration.Initialize();
            var (report, failed) = SelfTest.RunChecks();
            MessageBox.Show(report, "ProSight self-test", MessageBoxButtons.OK, failed == 0 ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
            return failed == 0 ? 0 : 1;
        }

        // Last-resort handlers: show the error instead of the process vanishing mid-show.
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) =>
            MessageBox.Show(e.Exception.Message, "ProSight error", MessageBoxButtons.OK, MessageBoxIcon.Error);
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            MessageBox.Show((e.ExceptionObject as Exception)?.Message ?? "Unknown error", "ProSight fatal error",
                MessageBoxButtons.OK, MessageBoxIcon.Error);

        ApplicationConfiguration.Initialize();
        Application.Run(new MainForm());
        return 0;
    }
}
