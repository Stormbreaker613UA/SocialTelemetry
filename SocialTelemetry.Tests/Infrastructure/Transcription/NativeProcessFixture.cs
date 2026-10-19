using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.Json;
using SocialTelemetry.Infrastructure.Transcription;

namespace SocialTelemetry.Tests.Infrastructure.Transcription;

// Synthetic executable entry in this test assembly. It exits before the xUnit/MTP entry point.
internal static class NativeProcessFixture
{
    internal static NativeTranscriptionHost Host => NativeTranscriptionHost.FromHostAssembly(typeof(Program).Assembly);
#pragma warning disable CA2255
    [ModuleInitializer]
    internal static void RunIfRequested()
    {
        var arguments = Environment.GetCommandLineArgs();
        if (arguments.Length < 4 || arguments[1] != "--native-process-fixture") return;
        Environment.Exit(Run(arguments[2], arguments[3], arguments.Skip(4).ToArray()));
    }
#pragma warning restore CA2255

    private static int Run(string role, string directory, string[] arguments)
    {
        Directory.CreateDirectory(directory);
        if (role == "escaped-grandchild" && OperatingSystem.IsLinux() && setsid() < 0) return 98;
        var identityRole = role == "escaped-grandchild" ? "grandchild" : role;
        WriteAtomically(Path.Combine(directory, identityRole + ".pid"), Identity(Environment.ProcessId));
        if (role == "arguments")
        {
            File.WriteAllText(Path.Combine(directory, "arguments.json"), JsonSerializer.Serialize(arguments));
            File.WriteAllText(Path.Combine(directory, "working-directory.txt"), Environment.CurrentDirectory);
            Console.Out.Write("stdout");
            Console.Error.Write("stderr");
            return 7;
        }
        if (role == "root")
        {
            using var child = Start("child", directory, arguments);
            WaitForFile(Path.Combine(directory, "grandchild.pid"));
            WriteAtomically(Path.Combine(directory, "ready"), "ready");
            WaitForFile(Path.Combine(directory, "action"));
            var action = File.ReadAllText(Path.Combine(directory, "action"));
            if (action == "output") Console.Error.Write(new string('s', 4096));
            if (action == "file") File.WriteAllBytes(Path.Combine(directory, "output.bin"), new byte[4096]);
            if (action is "exit" or "failure") return action == "failure" ? 7 : 0;
        }
        if (role == "child")
        {
            using var grandchild = Start(arguments.Contains("escape") ? "escaped-grandchild" : "grandchild", directory);
            WaitForFile(Path.Combine(directory, "grandchild.pid"));
        }
        // Self-expiration prevents a failed test from leaving synthetic sleepers indefinitely.
        Thread.Sleep(TimeSpan.FromSeconds(45));
        return 0;
    }

    internal static Process Start(string role, string directory, string[]? arguments = null)
    {
        var start = new ProcessStartInfo(Dotnet())
        {
            UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = directory
        };
        foreach (var argument in Arguments(role, directory)) start.ArgumentList.Add(argument);
        foreach (var argument in arguments ?? []) start.ArgumentList.Add(argument);
        return Process.Start(start) ?? throw new InvalidOperationException("Synthetic fixture could not start.");
    }

    [DllImport("libc", SetLastError = true)]
    private static extern int setsid();

    internal static string[] Arguments(string role, string directory) =>
        [typeof(NativeProcessFixture).Assembly.Location, "--native-process-fixture", role, directory];

    internal static string Dotnet()
    {
        var host = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH");
        if (host is not null && File.Exists(host)) return host;
        var executable = OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet";
        return (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator)
            .Select(directory => Path.Combine(directory, executable)).First(File.Exists);
    }

    internal static string Identity(int pid)
    {
        if (OperatingSystem.IsLinux())
        {
            var status = File.ReadAllText($"/proc/{pid}/stat");
            return $"{pid}|{status[(status.LastIndexOf(')') + 2)..].Split(' ')[19]}";
        }
        using var process = Process.GetProcessById(pid);
        return $"{pid}|{process.StartTime.ToUniversalTime().Ticks}";
    }

    private static void WaitForFile(string path)
    {
        var deadline = Stopwatch.StartNew();
        while (!File.Exists(path))
        {
            if (deadline.Elapsed > TimeSpan.FromSeconds(30)) Environment.Exit(99);
            Thread.Sleep(10);
        }
    }

    private static void WriteAtomically(string path, string text)
    {
        File.WriteAllText(path + ".new", text);
        File.Move(path + ".new", path);
    }
}
