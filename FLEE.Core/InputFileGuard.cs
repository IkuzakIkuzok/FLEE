using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace FLEE.Core;

internal static class InputFileGuard
{
    internal static void EnsureDifferent(string source, string output)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(output);
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (string.Equals(Resolve(source), Resolve(output), comparison) || SameWindowsFile(source, output))
            throw new ArgumentException("Output must be different from all input files.");
    }

    // Resolve links in parent directories as well as the filename itself.
    private static string Resolve(string path)
    {
        path = Path.GetFullPath(path);
        var root = Path.GetPathRoot(path)!;
        var current = root;
        foreach (var part in path[root.Length..].Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
        {
            if (part.Length == 0) continue;
            current = Path.Combine(current, part);
            FileSystemInfo info = Directory.Exists(current) ? new DirectoryInfo(current) : new FileInfo(current);
            if (info.LinkTarget is not null) current = info.ResolveLinkTarget(true)!.FullName;
        }
        return current;
    }

    private static bool SameWindowsFile(string source, string output)
    {
        if (!OperatingSystem.IsWindows() || !File.Exists(source) || !File.Exists(output)) return false;
        using var left = File.OpenHandle(source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var right = File.OpenHandle(output, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (!GetFileInformationByHandle(left, out var a) || !GetFileInformationByHandle(right, out var b))
            throw new IOException("Could not check input/output file identity.", new Win32Exception(Marshal.GetLastWin32Error()));
        return a.VolumeSerialNumber == b.VolumeSerialNumber && a.FileIndexHigh == b.FileIndexHigh && a.FileIndexLow == b.FileIndexLow;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileInformation
    {
        public uint Attributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME CreationTime, LastAccessTime, LastWriteTime;
        public uint VolumeSerialNumber, FileSizeHigh, FileSizeLow, NumberOfLinks, FileIndexHigh, FileIndexLow;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle file, out FileInformation information);
}
