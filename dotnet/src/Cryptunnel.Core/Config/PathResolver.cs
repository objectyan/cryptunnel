using System;
using System.IO;

namespace Cryptunnel.Core.Config;

public static class PathResolver
{
    public enum Mode { Portable, Installed }

    public static Mode Resolve(string exeDir)
    {
        return File.Exists(Path.Combine(exeDir, "portable.txt")) ? Mode.Portable : Mode.Installed;
    }

    public static string ConfigDir(string exeDir, Mode mode)
    {
        return mode == Mode.Portable
            ? Path.Combine(exeDir, "config.d")
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Cryptunnel", "config.d");
    }

    public static string LogDir(string exeDir, Mode mode)
    {
        return mode == Mode.Portable
            ? Path.Combine(exeDir, "logs")
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Cryptunnel", "logs");
    }

    public static string DataDir(string exeDir, Mode mode)
    {
        return mode == Mode.Portable
            ? Path.Combine(exeDir, "data")
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Cryptunnel", "data");
    }
}
