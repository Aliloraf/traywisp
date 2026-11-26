using System.Reflection;

const string Usage = """
    Usage: traywisp [option]

    Options:
      -h, --help       Show this help.
      --version        Show version information.

    Plugin commands are not implemented yet.
    """;

switch (args)
{
    case [] or ["-h"] or ["--help"]:
        Console.WriteLine(Usage);
        return 0;

    case ["--version"]:
        string? version = Assembly.GetEntryAssembly()?
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion;
        Console.WriteLine($"traywisp {version ?? "unknown"}");
        return 0;

    default:
        Console.Error.WriteLine($"traywisp: unrecognized arguments: {string.Join(' ', args)}");
        Console.Error.WriteLine(Usage);
        return 2;
}
