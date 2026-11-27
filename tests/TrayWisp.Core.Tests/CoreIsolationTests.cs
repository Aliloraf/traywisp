using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using TrayWisp.Core.Plugins;
using Xunit;

namespace TrayWisp.Core.Tests;

// Guards the rule in docs/architecture.md that Core has no file system, process or clock access,
// by inspecting the type and member references compiled into TrayWisp.Core.dll.
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
        "System.Diagnostics.Process",
        "System.Diagnostics.ProcessStartInfo",
        "System.Diagnostics.Stopwatch",
        "System.TimeProvider",
        "System.Threading.Timer",
        "System.Net.Http.HttpClient",
        "System.Xml.XmlReader",
        "System.Xml.XmlDocument",
        "System.Text.RegularExpressions.Regex",
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
    ];

    [Fact]
    public void CoreReferencesNoFileSystemProcessClockNetworkXmlOrRegexTypes()
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
