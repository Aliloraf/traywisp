using System.Diagnostics;
using TrayWisp.Core.Execution;

namespace TrayWisp.Runtime.Execution;

// The fixed interpreter arguments and the child environment policy. See docs/execution-engine.md ("Fixed
// interpreter arguments" and "Environment"). Nothing here is configurable by plugins, their metadata, their
// output or callers of the public API.
internal static class InterpreterCommand
{
    // Interpreter-specific variables that can inject startup code or change module or import resolution, removed from
    // the child environment (names compared ordinal case-insensitively). This is not a sandbox: interpreters have
    // other configuration mechanisms (configuration files, registry, site-packages).
    //
    // PSExecutionPolicyPreference is deliberately NOT removed: it is the PowerShell execution policy of the process
    // scope, which TrayWisp inherits like every other variable. Removing it could replace a restrictive process-level
    // policy with a less restrictive one from another scope. TrayWisp neither tightens nor weakens the effective policy.
    private static readonly string[] RemovedPrefixes =
    [
        // Python: PYTHONPATH, PYTHONHOME, PYTHONSTARTUP, PYTHONUSERBASE, PYTHONINSPECT, PYTHONIOENCODING, ...
        "PYTHON",

        // Node.js: NODE_OPTIONS (--require/--import), NODE_PATH, NODE_REPL_EXTERNAL_MODULE, NODE_EXTRA_CA_CERTS, ...
        "NODE_",

        // .NET profilers, which load native code into PowerShell 7 and Windows PowerShell.
        "COR_PROFILER",
        "CORECLR_PROFILER",
    ];

    private static readonly string[] RemovedNames =
    [
        // .NET (PowerShell 7) startup code and dependency injection.
        "DOTNET_STARTUP_HOOKS",
        "DOTNET_ADDITIONAL_DEPS",
        "DOTNET_SHARED_STORE",
        "CORECLR_ENABLE_PROFILING",

        // .NET Framework (Windows PowerShell) profiler and AppDomainManager injection.
        "COR_ENABLE_PROFILING",
        "APPDOMAIN_MANAGER_ASM",
        "APPDOMAIN_MANAGER_TYPE",
    ];

    // The complete argument list after the interpreter path. The script path is always one separate argument.
    public static string[] Arguments(InterpreterKind kind, string scriptPath) => kind switch
    {
        // -File runs the script under the effective execution policy (including an inherited process-scope policy);
        // -Command and -ExecutionPolicy are never used.
        InterpreterKind.PowerShell or InterpreterKind.WindowsPowerShell => ["-NoLogo", "-NoProfile", "-NonInteractive", "-File", scriptPath],

        // -E ignores PYTHON* variables, -u unbuffers the output streams, -X utf8 makes standard output UTF-8. The
        // script's directory stays first on sys.path, so plugin-local imports work, and site-packages (including the
        // user's) stay enabled.
        InterpreterKind.Python => ["-E", "-u", "-X", "utf8", scriptPath],

        // "--" ends Node.js options, so the script path can only be a script path.
        InterpreterKind.Node => ["--", scriptPath],
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown interpreter."),
    };

    public static bool IsRemovedVariable(string name)
    {
        foreach (string removed in RemovedNames)
        {
            if (name.Equals(removed, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        foreach (string prefix in RemovedPrefixes)
        {
            if (name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    // Prepares a direct launch, before authorization: no shell, redirected standard streams, no window and the
    // filtered environment. The script path, arguments and working directory are added by SetScript only after the
    // trust check, from its validated path.
    public static ProcessStartInfo Prepare(PluginInterpreter interpreter, IReadOnlyDictionary<string, string>? baseEnvironment)
    {
        var startInfo = new ProcessStartInfo(interpreter.Path)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            ErrorDialog = false,
        };

        IDictionary<string, string?> environment = startInfo.Environment;
        if (baseEnvironment is not null)
        {
            environment.Clear();
            foreach ((string name, string value) in baseEnvironment)
            {
                environment[name] = value;
            }
        }

        foreach (string name in environment.Keys.Where(IsRemovedVariable).ToList())
        {
            environment.Remove(name);
        }

        return startInfo;
    }

    // Adds the fixed arguments with the validated script path, and the script's own directory as working directory.
    public static void SetScript(ProcessStartInfo startInfo, InterpreterKind kind, string scriptPath)
    {
        startInfo.ArgumentList.Clear();
        foreach (string argument in Arguments(kind, scriptPath))
        {
            startInfo.ArgumentList.Add(argument);
        }

        startInfo.WorkingDirectory = Path.GetDirectoryName(scriptPath);
    }
}
