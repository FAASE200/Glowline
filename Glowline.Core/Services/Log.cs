using System;
using System.IO;

namespace Glowline.Services;

public static class Log
{
    static readonly object Gate = new();

    public static string LogFile => Path.Combine(StoragePaths.DataDirectory, "log.txt");

    public static string PreviousLogFile => Path.Combine(StoragePaths.DataDirectory, "log.old.txt");

    public static void Write(string message)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(StoragePaths.DataDirectory);
                var fi = new FileInfo(LogFile);
                if (fi.Exists && fi.Length > 512 * 1024)
                {
                    // Keep the previous log as log.old.txt so an error right
                    // before the rollover still shows up in bug reports.
                    File.Move(LogFile, PreviousLogFile, overwrite: true);
                }
                File.AppendAllText(LogFile, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {message}{Environment.NewLine}");
            }
        }
        catch
        {
        }
    }
}
