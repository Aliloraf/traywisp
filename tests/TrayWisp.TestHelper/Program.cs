using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;

// A controlled stand-in for an interpreter (see the project file). The script, its last argument, holds one
// instruction per line; arguments of an instruction are separated by '|'. Blank lines and lines starting with '#'
// are ignored. An unknown instruction exits with code 99.
//
//   out <text>                 writes text and LF to standard output (UTF-8)
//   out-nonl <text>            writes text without a line feed
//   out-hex <hex>              writes raw bytes
//   out-fill <count>           writes count bytes of 'a'
//   err <text>                 writes text and LF to standard error
//   err-fill <count>           writes count bytes of 'e' to standard error
//   both-fill <count>          writes count bytes to each stream, alternating in small chunks
//   report <name>|<name>...    writes one JSON line: arguments, working directory, standard input bytes, variables
//   sleep <ms>                 sleeps
//   write-file <path>|<text>   creates a file (a marker that the script ran)
//   wait-file <path>|<ms>      waits until a file exists, then continues (exit 98 if it never appears)
//   spawn-holder <pidfile>|<ms>    starts a copy of this program that inherits the output pipes and sleeps
//   spawn-detached <pidfile>|<ms>  starts a copy through the shell, without the output pipes, that sleeps
//   spawn-releaser <pidfile>|<ms>  starts a copy that inherits the output pipes, closes them, then sleeps
//   exit <code>                exits
if (args.Length >= 3 && args[0] is "--hold" or "--release")
{
    if (args[0] == "--release")
    {
        // Closes the inherited standard handles, which are the redirected pipes of the plugin process.
        foreach (int standardHandle in new[] { -10, -11, -12 })
        {
            CloseHandle(GetStdHandle(standardHandle));
        }
    }

    WritePid(args[2]);
    Thread.Sleep(int.Parse(args[1], CultureInfo.InvariantCulture));
    return 0;
}

if (args.Length == 0)
{
    return 97;
}

Stream stdout = Console.OpenStandardOutput();
Stream stderr = Console.OpenStandardError();
foreach (string rawLine in File.ReadAllLines(args[^1], Encoding.UTF8))
{
    string line = rawLine.Trim();
    if (line.Length == 0 || line.StartsWith('#'))
    {
        continue;
    }

    int space = line.IndexOf(' ', StringComparison.Ordinal);
    string verb = space < 0 ? line : line[..space];
    string rest = space < 0 ? string.Empty : line[(space + 1)..];
    string[] parts = rest.Split('|', StringSplitOptions.TrimEntries);
    switch (verb)
    {
        case "out":
            Write(stdout, Encoding.UTF8.GetBytes(rest + "\n"));
            break;
        case "out-nonl":
            Write(stdout, Encoding.UTF8.GetBytes(rest));
            break;
        case "out-hex":
            Write(stdout, Convert.FromHexString(rest.Replace(" ", string.Empty, StringComparison.Ordinal)));
            break;
        case "out-fill":
            Fill(stdout, int.Parse(rest, CultureInfo.InvariantCulture), (byte)'a');
            break;
        case "err":
            Write(stderr, Encoding.UTF8.GetBytes(rest + "\n"));
            break;
        case "err-fill":
            Fill(stderr, int.Parse(rest, CultureInfo.InvariantCulture), (byte)'e');
            break;
        case "both-fill":
            int total = int.Parse(rest, CultureInfo.InvariantCulture);
            for (int written = 0; written < total; written += 1000)
            {
                Fill(stdout, Math.Min(1000, total - written), (byte)'a');
                Fill(stderr, Math.Min(1000, total - written), (byte)'e');
            }

            break;
        case "report":
            Write(stdout, Report(args, rest.Length == 0 ? [] : parts));
            break;
        case "sleep":
            Thread.Sleep(int.Parse(rest, CultureInfo.InvariantCulture));
            break;
        case "write-file":
            File.WriteAllText(parts[0], parts.Length > 1 ? parts[1] : string.Empty);
            break;
        case "wait-file":
            var watch = Stopwatch.StartNew();
            while (!File.Exists(parts[0]))
            {
                if (watch.ElapsedMilliseconds > int.Parse(parts[1], CultureInfo.InvariantCulture))
                {
                    return 98;
                }

                Thread.Sleep(10);
            }

            break;
        case "spawn-holder":
            Spawn(parts[0], parts[1], shell: false, "--hold");
            break;
        case "spawn-detached":
            Spawn(parts[0], parts[1], shell: true, "--hold");
            break;
        case "spawn-releaser":
            Spawn(parts[0], parts[1], shell: false, "--release");
            break;
        case "exit":
            stdout.Flush();
            stderr.Flush();
            return int.Parse(rest, CultureInfo.InvariantCulture);
        default:
            return 99;
    }
}

stdout.Flush();
stderr.Flush();
return 0;

static void Write(Stream stream, byte[] bytes)
{
    stream.Write(bytes);
    stream.Flush();
}

static void Fill(Stream stream, int count, byte value)
{
    byte[] chunk = new byte[Math.Min(count, 64 * 1024)];
    Array.Fill(chunk, value);
    for (int written = 0; written < count; written += chunk.Length)
    {
        stream.Write(chunk, 0, Math.Min(chunk.Length, count - written));
    }

    stream.Flush();
}

static byte[] Report(string[] args, string[] variables)
{
    long stdin = 0;
    using (Stream input = Console.OpenStandardInput())
    {
        byte[] buffer = new byte[4096];
        int read;
        while ((read = input.Read(buffer)) > 0)
        {
            stdin += read;
        }
    }

    using var memory = new MemoryStream();
    using (var json = new Utf8JsonWriter(memory))
    {
        json.WriteStartObject();
        json.WriteStartArray("args");
        foreach (string argument in args)
        {
            json.WriteStringValue(argument);
        }

        json.WriteEndArray();
        json.WriteString("cwd", Environment.CurrentDirectory);
        json.WriteNumber("stdin", stdin);
        json.WriteStartObject("env");
        foreach (string name in variables)
        {
            json.WriteString(name, Environment.GetEnvironmentVariable(name));
        }

        json.WriteEndObject();
        json.WriteEndObject();
    }

    memory.WriteByte((byte)'\n');
    return memory.ToArray();
}

static void Spawn(string pidFile, string milliseconds, bool shell, string mode)
{
    var startInfo = new ProcessStartInfo(Environment.ProcessPath!)
    {
        // Without the shell, the copy inherits this process's console and every inheritable handle, including the
        // redirected output pipes. Through the shell it inherits no handles.
        UseShellExecute = shell,
        WindowStyle = shell ? ProcessWindowStyle.Hidden : ProcessWindowStyle.Normal,
    };
    startInfo.ArgumentList.Add(mode);
    startInfo.ArgumentList.Add(milliseconds);
    startInfo.ArgumentList.Add(pidFile);
    using Process child = Process.Start(startInfo)!;

    // Waits until the child has recorded itself, so the test can always find it.
    var watch = Stopwatch.StartNew();
    while (!File.Exists(pidFile) && watch.ElapsedMilliseconds < 10_000)
    {
        Thread.Sleep(5);
    }
}

static void WritePid(string pidFile)
{
    using Process self = Process.GetCurrentProcess();
    string temporary = pidFile + ".tmp";
    File.WriteAllText(temporary, string.Create(CultureInfo.InvariantCulture, $"{self.Id} {self.StartTime.ToUniversalTime().Ticks}"));
    File.Move(temporary, pidFile);
}

[System.Runtime.InteropServices.DllImport("kernel32.dll")]
[System.Runtime.InteropServices.DefaultDllImportSearchPaths(System.Runtime.InteropServices.DllImportSearchPath.System32)]
static extern nint GetStdHandle(int standardHandle);

[System.Runtime.InteropServices.DllImport("kernel32.dll")]
[System.Runtime.InteropServices.DefaultDllImportSearchPaths(System.Runtime.InteropServices.DllImportSearchPath.System32)]
[return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
static extern bool CloseHandle(nint handle);
