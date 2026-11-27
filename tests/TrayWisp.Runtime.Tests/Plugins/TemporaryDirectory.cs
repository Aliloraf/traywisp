using System.Diagnostics;
using System.Text;

namespace TrayWisp.Runtime.Tests.Plugins;

// A uniquely named directory under the system temporary directory, deleted on dispose.
// Reparse points inside it are removed without following them.
internal sealed class TemporaryDirectory : IDisposable
{
    private readonly List<Action> _cleanup = [];

    public TemporaryDirectory()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "traywisp-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public string Combine(string relativePath) =>
        System.IO.Path.Combine(Path, relativePath.Replace('/', System.IO.Path.DirectorySeparatorChar));

    public string CreateDirectory(string relativePath) => Directory.CreateDirectory(Combine(relativePath)).FullName;

    public string WriteFile(string relativePath, string content = "") => WriteBytes(relativePath, Encoding.UTF8.GetBytes(content));

    public string WriteBytes(string relativePath, byte[] content)
    {
        string fullPath = Combine(relativePath);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(fullPath)!);
        File.WriteAllBytes(fullPath, content);
        return fullPath;
    }

    // Registers an action to run before the directory is deleted (for example restoring permissions).
    public void OnDispose(Action action) => _cleanup.Add(action);

    public void Dispose()
    {
        foreach (Action action in Enumerable.Reverse(_cleanup))
        {
            action();
        }

        if (!Directory.Exists(Path))
        {
            return;
        }

        PrepareForDeletion(new DirectoryInfo(Path));
        Directory.Delete(Path, recursive: true);
    }

    // Removes links without following them (recursive deletion can fail on junctions) and clears
    // attributes that would block deletion.
    private static void PrepareForDeletion(DirectoryInfo directory)
    {
        const FileAttributes Restrictive = FileAttributes.ReadOnly | FileAttributes.Hidden | FileAttributes.System;
        foreach (FileSystemInfo entry in directory.EnumerateFileSystemInfos("*", new EnumerationOptions { AttributesToSkip = 0 }))
        {
            if ((entry.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                if (entry is DirectoryInfo link)
                {
                    link.Delete();
                }
                else
                {
                    entry.Delete();
                }

                continue;
            }

            if ((entry.Attributes & Restrictive) != 0)
            {
                entry.Attributes &= ~Restrictive;
            }

            if (entry is DirectoryInfo subdirectory)
            {
                PrepareForDeletion(subdirectory);
            }
        }
    }

    // Creates a directory junction with mklink (no special privileges needed). Returns false when
    // junctions are unavailable, for example on non-Windows platforms.
    public static bool TryCreateJunction(string linkPath, string targetPath)
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        return RunQuietly("cmd.exe", "/d", "/c", "mklink", "/J", linkPath, targetPath) && Directory.Exists(linkPath);
    }

    // Symbolic links need Developer Mode or elevation on Windows.
    public static bool TryCreateDirectorySymbolicLink(string linkPath, string targetPath)
    {
        try
        {
            Directory.CreateSymbolicLink(linkPath, targetPath);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            return false;
        }
    }

    public static bool TryCreateFileSymbolicLink(string linkPath, string targetPath)
    {
        try
        {
            File.CreateSymbolicLink(linkPath, targetPath);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            return false;
        }
    }

    // Makes an empty directory case-sensitive. On Windows this uses the per-directory case
    // sensitivity flag, which requires the Windows Subsystem for Linux optional component.
    // Elsewhere it succeeds only if the file system is already case-sensitive.
    public static bool TryMakeCaseSensitive(string directory)
    {
        if (OperatingSystem.IsWindows())
        {
            RunQuietly("fsutil.exe", "file", "setCaseSensitiveInfo", directory, "enable");
        }

        string probe = System.IO.Path.Combine(directory, "probe.tmp");
        string upperProbe = System.IO.Path.Combine(directory, "PROBE.tmp");
        File.WriteAllText(probe, "a");
        try
        {
            return !File.Exists(upperProbe);
        }
        finally
        {
            File.Delete(probe);
        }
    }

    private static bool RunQuietly(string fileName, params string[] arguments)
    {
        var startInfo = new ProcessStartInfo(fileName)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (string argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        try
        {
            using Process? process = Process.Start(startInfo);
            if (process is null)
            {
                return false;
            }

            process.StandardOutput.ReadToEnd();
            process.StandardError.ReadToEnd();
            process.WaitForExit();
            return process.ExitCode == 0;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }
}
