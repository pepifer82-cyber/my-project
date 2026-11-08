using System.Threading;

namespace ATPrinterAgent;

internal static class Program
{
    private const string MutexName = @"Local\ATPrinterAgent_v026_Instance";
    private const string ShowEventName = @"Local\ATPrinterAgent_v026_Show";
    private static Mutex? _instanceMutex;
    private static EventWaitHandle? _showEvent;

    [STAThread]
    static void Main()
    {
        var args = Environment.GetCommandLineArgs();
        bool background = args.Any(a => a.Equals("--background", StringComparison.OrdinalIgnoreCase));
        bool createdNew;
        _instanceMutex = new Mutex(true, MutexName, out createdNew);

        if (!createdNew)
        {
            try
            {
                using var signal = EventWaitHandle.OpenExisting(ShowEventName);
                signal.Set();
            }
            catch { }
            return;
        }

        _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
        ApplicationConfiguration.Initialize();
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.Run(new MainForm(background));
        _showEvent.Dispose();
        _instanceMutex.ReleaseMutex();
        _instanceMutex.Dispose();
    }

    public static bool ConsumeShowRequest()
    {
        try { return _showEvent?.WaitOne(0) == true; }
        catch { return false; }
    }
}
