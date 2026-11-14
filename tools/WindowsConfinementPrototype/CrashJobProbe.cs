using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text.Json;

namespace WindowsConfinementPrototype;

internal static class CrashJobProbe
{
    internal static async Task ControllerAsync(string executable, string root, string sidText)
    {
        if (!Path.IsPathFullyQualified(root) || !Path.GetFileName(root).StartsWith("AIPlatform.Preview.Prototype.", StringComparison.Ordinal) ||
            Path.GetDirectoryName(executable) != root || !sidText.StartsWith("S-1-15-2-", StringComparison.Ordinal))
            throw new ArgumentException("Invalid dedicated crash probe identity.");
        var sid = new SecurityIdentifier(sidText);
        var bytes = new byte[sid.BinaryLength];
        sid.GetBinaryForm(bytes, 0);
        var memory = Marshal.AllocHGlobal(bytes.Length);
        try
        {
            Marshal.Copy(bytes, 0, memory, bytes.Length);
            await RestrictedProcess.RunAsync(executable, ["--input-type=module", "-e", "setInterval(()=>{},1000)"],
                root, "crash-job", memory, TimeSpan.FromSeconds(30),
                pid =>
                {
                    var temporary = Path.Combine(root, "crash-child.tmp");
                    File.WriteAllText(temporary, JsonSerializer.Serialize(new { pid }));
                    File.Move(temporary, Path.Combine(root, "crash-child.json"));
                });
        }
        finally { Marshal.FreeHGlobal(memory); }
    }

    internal static async Task<object> RunAsync(string executable, string root, string sid)
    {
        var host = Environment.ProcessPath ?? throw new IOException("Missing prototype host.");
        var start = new ProcessStartInfo(host) { UseShellExecute = false, CreateNoWindow = true };
        if (Path.GetFileNameWithoutExtension(host).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            start.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "WindowsConfinementPrototype.dll"));
        foreach (var argument in new[] { "--crash-job", executable, root, sid }) start.ArgumentList.Add(argument);
        using var controller = Process.Start(start) ?? throw new IOException("Crash probe controller failed to start.");
        Process? child = null;
        var forcedChildCleanup = false;
        try
        {
            var marker = Path.Combine(root, "crash-child.json");
            for (var attempt = 0; attempt < 100 && !File.Exists(marker) && !controller.HasExited; attempt++)
                await Task.Delay(50);
            if (!File.Exists(marker)) throw new IOException("Crash probe child startup was not confirmed.");
            using var document = JsonDocument.Parse(await File.ReadAllTextAsync(marker));
            var pid = document.RootElement.GetProperty("pid").GetUInt32();
            child = Process.GetProcessById(checked((int)pid));
            _ = child.Handle;
            var token = NativeWindows.Token(pid);
            if (!token.AppContainer || token.Sid != sid || child.HasExited)
                throw new IOException("Crash probe child was not alive under the expected AppContainer.");
            controller.Kill(entireProcessTree: false);
            await controller.WaitForExitAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await child.WaitForExitAsync(timeout.Token);
            return new { controllerKilled = true, childExitedByJobClose = child.HasExited, childToken = token };
        }
        finally
        {
            if (!controller.HasExited)
            {
                controller.Kill(entireProcessTree: false);
                await controller.WaitForExitAsync();
            }
            if (child is { HasExited: false })
            {
                forcedChildCleanup = true;
                child.Kill(entireProcessTree: true);
                await child.WaitForExitAsync();
            }
            child?.Dispose();
            if (forcedChildCleanup) Console.Error.WriteLine("Crash probe failed; owned child required forced cleanup.");
        }
    }
}
