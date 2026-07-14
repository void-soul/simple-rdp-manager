using System.Diagnostics;
using System.Text;

namespace SimpleRdpManager;

public static class RdpLogger
{
    private static readonly string LogDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "SimpleRdpManager", "logs");

    private static readonly string LogFile = Path.Combine(LogDir, "simple-rdp.log");

    public static void Log(string msg)
    {
        try
        {
            if (!Directory.Exists(LogDir))
                Directory.CreateDirectory(LogDir);

            var line = $"[{DateTime.Now:HH:mm:ss.fff}] {msg}";
            Debug.WriteLine($"[RDP] {line}");
            File.AppendAllText(LogFile, line + Environment.NewLine, Encoding.UTF8);
        }
        catch { }
    }
}
