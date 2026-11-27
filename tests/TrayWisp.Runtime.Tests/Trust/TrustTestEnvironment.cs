using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using TrayWisp.Core.Plugins;
using TrayWisp.Core.Trust;
using TrayWisp.Runtime.Tests.Plugins;
using TrayWisp.Runtime.Trust;
using Xunit;

namespace TrayWisp.Runtime.Tests.Trust;

// An isolated plugin root and trust store under a new temporary directory. The paths are canonical
// (long names, as the trust checks require), so tests also work where the temporary directory is reached
// through a short name such as C:\Users\RUNNER~1. Tests never touch the real per-user trust store.
[SupportedOSPlatform("windows")]
internal sealed class TrustTestEnvironment : IDisposable
{
    private readonly TemporaryDirectory _directory = new();

    public TrustTestEnvironment()
    {
        Base = Canonicalize(_directory.Path);
        Root = Directory.CreateDirectory(Path.Combine(Base, "plugins")).FullName;
        StoreDirectory = Path.Combine(Base, "store");
        StorePath = Path.Combine(StoreDirectory, "approvals.json");
    }

    // Skips the calling test unless it runs on Windows: the trust checks rely on Windows file APIs.
    public static TrustTestEnvironment Create()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Plugin trust operations are implemented for Windows only.");
        return new TrustTestEnvironment();
    }

    public string Base { get; }

    public string Root { get; }

    public string StoreDirectory { get; }

    public string StorePath { get; }

    public static CancellationToken Token => TestContext.Current.CancellationToken;

    public PluginTrustService Service(PluginTrustOptions? options = null, TrustTestHooks? hooks = null, TimeProvider? time = null) =>
        new(StorePath, options, time ?? TimeProvider.System, hooks);

    public string Write(string relativePath, string content) => WriteBytes(relativePath, Encoding.UTF8.GetBytes(content));

    public string WriteBytes(string relativePath, byte[] content)
    {
        string path = PluginPath(relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, content);
        return path;
    }

    public string PluginPath(string relativePath) => Path.Combine(Root, relativePath.Replace('/', '\\'));

    public string StoreText() => File.ReadAllText(StorePath);

    public void OnDispose(Action action) => _directory.OnDispose(action);

    // Denies the current user reading the file's data until the environment is disposed.
    public void DenyReading(string file) => AddDenyRule(new FileInfo(file), FileSystemRights.ReadData);

    // Denies the current user listing the directory until the environment is disposed.
    public void DenyListing(string directory) => AddDenyRule(new DirectoryInfo(directory), FileSystemRights.ListDirectory | FileSystemRights.ReadData);

    private void AddDenyRule(FileSystemInfo target, FileSystemRights rights)
    {
        var rule = new FileSystemAccessRule(WindowsIdentity.GetCurrent().User!, rights, AccessControlType.Deny);
        if (target is DirectoryInfo directory)
        {
            DirectorySecurity security = directory.GetAccessControl();
            security.AddAccessRule(rule);
            directory.SetAccessControl(security);
            OnDispose(() =>
            {
                DirectorySecurity restored = directory.GetAccessControl();
                restored.RemoveAccessRule(rule);
                directory.SetAccessControl(restored);
            });
        }
        else
        {
            var file = (FileInfo)target;
            FileSecurity security = file.GetAccessControl();
            security.AddAccessRule(rule);
            file.SetAccessControl(security);
            OnDispose(() =>
            {
                FileSecurity restored = file.GetAccessControl();
                restored.RemoveAccessRule(rule);
                file.SetAccessControl(restored);
            });
        }
    }

    // Inspects and approves a plugin the way a user interface would: review, then approve what was reviewed.
    public PluginApproval Approve(PluginTrustService service, string relativePath, string? root = null)
    {
        PluginInspectionResult inspection = service.Inspect(root ?? Root, PluginId.Create(relativePath), Token);
        Assert.Equal(PluginInspectionStatus.Inspected, inspection.Status);
        PluginApprovalResult result = service.Approve(root ?? Root, PluginId.Create(relativePath), inspection.Plugin!.Fingerprint, Token);
        Assert.Equal(PluginApprovalStatus.Approved, result.Status);
        return result.Approval!;
    }

    public PluginTrustDecision Check(PluginTrustService service, string relativePath, string? root = null) =>
        service.CheckTrust(root ?? Root, PluginId.Create(relativePath), Token);

    // Creates a hard link with fsutil, which needs no elevation on NTFS. Returns false where hard links are unavailable.
    public static bool TryCreateHardLink(string link, string existing)
    {
        var startInfo = new System.Diagnostics.ProcessStartInfo("fsutil.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (string argument in new[] { "hardlink", "create", link, existing })
        {
            startInfo.ArgumentList.Add(argument);
        }

        try
        {
            using System.Diagnostics.Process process = System.Diagnostics.Process.Start(startInfo)!;
            process.StandardOutput.ReadToEnd();
            process.StandardError.ReadToEnd();
            process.WaitForExit();
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return false;
        }

        return File.Exists(link);
    }

    // Renames the store directory away and back, which fails while any operation still holds it open.
    public void AssertStoreDirectoryReleased()
    {
        string moved = StoreDirectory + "-released";
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                Directory.Move(StoreDirectory, moved);
                break;
            }
            catch (IOException) when (attempt < 5)
            {
                // Tolerates a brief open by another program, such as an antivirus scanner.
                Thread.Sleep(50);
            }
        }

        Directory.Move(moved, StoreDirectory);
    }

    public static string Canonicalize(string path)
    {
        using Microsoft.Win32.SafeHandles.SafeFileHandle handle = WindowsFileApi.Open(Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)), WindowsFileApi.OpenMode.Inspect, out int error)
            ?? throw new IOException($"Could not open {path}: error {error}");
        Assert.True(WindowsFileApi.TryGetFinalPath(handle, out string final, out _));
        return final;
    }

    public void Dispose() => _directory.Dispose();
}
