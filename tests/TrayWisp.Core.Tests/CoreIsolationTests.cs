using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using TrayWisp.Core.Plugins;
using Xunit;

namespace TrayWisp.Core.Tests;

// Guards the rule in docs/architecture.md that Core has no file system, process, clock, network or
// environment access, by inspecting the type and member references compiled into TrayWisp.Core.dll.
public sealed class CoreIsolationTests
{
    private static readonly string[] ForbiddenTypes =
    [
        "System.IO.File",
        "System.IO.FileInfo",
        "System.IO.FileStream",
        "System.IO.FileSystemInfo",
        "System.IO.FileSystemWatcher",
        "System.IO.Directory",
        "System.IO.DirectoryInfo",
        "System.IO.DriveInfo",
        "System.IO.Path",
        "System.IO.StreamReader",
        "System.IO.StreamWriter",
        "System.IO.Enumeration.FileSystemEnumerable`1",
        "System.IO.RandomAccess",
        "Microsoft.Win32.SafeHandles.SafeFileHandle",
        "System.Runtime.InteropServices.Marshal",
        "System.Diagnostics.Process",
        "System.Diagnostics.ProcessStartInfo",
        "Microsoft.Win32.SafeHandles.SafeProcessHandle",
        "System.IO.Pipes.AnonymousPipeServerStream",
        "System.IO.Pipes.PipeStream",
        "System.Console",
        "System.Diagnostics.Stopwatch",
        "System.TimeProvider",
        "System.Threading.Timer",
        "System.Net.Http.HttpClient",
        "System.Net.Dns",
        "System.Net.WebClient",
        "System.Net.WebRequest",
        "System.Net.Sockets.Socket",
        "System.Drawing.Color",
        "System.Xml.XmlReader",
        "System.Xml.XmlDocument",
        "System.Text.RegularExpressions.Regex",

        // Trust: Core holds models and pure decisions only. Hashing file content belongs to Runtime.
        "System.Security.Cryptography.IncrementalHash",
        "System.Security.Cryptography.SHA256",
        "System.Security.Cryptography.HashAlgorithm",
    ];

    private static readonly string[] ForbiddenMembers =
    [
        "System.DateTime.get_Now",
        "System.DateTime.get_UtcNow",
        "System.DateTime.get_Today",
        "System.DateTimeOffset.get_Now",
        "System.DateTimeOffset.get_UtcNow",
        "System.Environment.get_TickCount",
        "System.Environment.get_TickCount64",

        // Output actions are kept exactly as written: nothing may expand environment variables or
        // resolve paths against the current environment.
        "System.Environment.GetEnvironmentVariable",
        "System.Environment.GetEnvironmentVariables",
        "System.Environment.ExpandEnvironmentVariables",
        "System.Environment.GetFolderPath",
        "System.Environment.get_CurrentDirectory",

        // Approval generations are created in Runtime, never in Core.
        "System.Guid.NewGuid",

        // Execution: interpreter resolution and process environments belong to Runtime.
        "System.Environment.get_SystemDirectory",
        "System.Environment.get_ProcessPath",
        "System.Environment.get_ProcessId",
        "System.Threading.Tasks.Task.Delay",
        "System.Threading.Tasks.Task.Run",
    ];

    [Fact]
    public void CoreReferencesNoFileSystemProcessClockNetworkEnvironmentXmlOrRegexTypes()
    {
        using var stream = File.OpenRead(typeof(PluginId).Assembly.Location);
        using var reader = new PEReader(stream);
        MetadataReader metadata = reader.GetMetadataReader();

        string[] referencedTypes = [.. metadata.TypeReferences.Select(handle => FullName(metadata, handle))];
        string[] referencedMembers =
        [
            .. metadata.MemberReferences
                .Select(handle => metadata.GetMemberReference(handle))
                .Where(member => member.Parent.Kind == HandleKind.TypeReference)
                .Select(member => FullName(metadata, (TypeReferenceHandle)member.Parent) + "." + metadata.GetString(member.Name)),
        ];

        Assert.Empty(referencedTypes.Intersect(ForbiddenTypes));
        Assert.Empty(referencedMembers.Intersect(ForbiddenMembers));

        // Sanity check that the inspection sees real references.
        Assert.Contains("System.String", referencedTypes);
        Assert.Contains("System.Guid", referencedTypes);
    }

    [Fact]
    public void CoreExecutionTypesArePureModels()
    {
        // Core's execution namespace holds diagnostics, statuses and pure decoding only: no type there owns a process,
        // handle, stream or task.
        Type[] types = [.. typeof(PluginId).Assembly.GetTypes().Where(type => type.Namespace == "TrayWisp.Core.Execution")];
        Assert.NotEmpty(types);
        foreach (Type type in types)
        {
            Assert.False(typeof(IDisposable).IsAssignableFrom(type), type.FullName);
            Assert.All(
                type.GetMembers(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.DeclaredOnly)
                    .OfType<System.Reflection.MethodInfo>(),
                method => Assert.False(typeof(Task).IsAssignableFrom(method.ReturnType) || typeof(Stream).IsAssignableFrom(method.ReturnType), $"{type.FullName}.{method.Name}"));
        }
    }

    [Fact]
    public void CoreSchedulingTypesArePureModels()
    {
        // Scheduling policy and states only: no type owns a timer, task, handle or stream, and none reads a clock (the
        // member references are covered by the test above).
        Type[] types = [.. typeof(PluginId).Assembly.GetTypes().Where(type => type.Namespace == "TrayWisp.Core.Scheduling")];
        Assert.NotEmpty(types);
        foreach (Type type in types)
        {
            Assert.False(typeof(IDisposable).IsAssignableFrom(type), type.FullName);
            Assert.All(
                type.GetMembers(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.DeclaredOnly)
                    .OfType<System.Reflection.MethodInfo>(),
                method => Assert.False(typeof(Task).IsAssignableFrom(method.ReturnType) || typeof(Stream).IsAssignableFrom(method.ReturnType), $"{type.FullName}.{method.Name}"));
        }
    }

    private static string FullName(MetadataReader metadata, TypeReferenceHandle handle)
    {
        TypeReference type = metadata.GetTypeReference(handle);
        string name = metadata.GetString(type.Name);
        return type.ResolutionScope.Kind == HandleKind.TypeReference
            ? FullName(metadata, (TypeReferenceHandle)type.ResolutionScope) + "+" + name
            : metadata.GetString(type.Namespace) + "." + name;
    }
}
