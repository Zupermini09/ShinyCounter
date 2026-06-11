using System.IO;
using System.Windows;

namespace ShinyCounter;

public partial class App : Application
{
    public App()
    {
        DispatcherUnhandledException += (_, e) =>
        {
            try
            {
                File.AppendAllText(
                    Path.Combine(Path.GetTempPath(), "ShinyCounter-crash.log"),
                    $"{DateTime.Now:u} {e.Exception}\n\n");
            }
            catch { }
        };
    }
}
