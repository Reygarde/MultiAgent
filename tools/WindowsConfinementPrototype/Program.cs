using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using WindowsConfinementPrototype;
using static WindowsConfinementPrototype.NativeWindows;

if (OperatingSystem.IsWindows() && args.Length == 4 && args[0] == "--crash-job")
{
    await CrashJobProbe.ControllerAsync(args[1], args[2], args[3]);
    return;
}

if (!OperatingSystem.IsWindows() || args.Length != 4 || args[0] != "--node" || args[2] != "--report" ||
    !Path.IsPathFullyQualified(args[1]) || !File.Exists(args[1]) || !Path.IsPathFullyQualified(args[3]))
    throw new ArgumentException("Use --node <trusted absolute node.exe> --report <absolute new JSON file> on Windows.");
var reportPath = args[3];
if (File.Exists(reportPath)) throw new IOException("Refusing to overwrite an existing prototype report.");
Environment.ExitCode = 2;
var name = "AIPlatform.Preview.Prototype." + Guid.NewGuid().ToString("N");
var root = Path.Combine(Path.GetTempPath(), name);
var report = new Dictionary<string, object?>
{
    ["windows"] = Environment.OSVersion.VersionString,
    ["prototypeVersion"] = "6.5-D",
    ["startedAtUtc"] = DateTimeOffset.UtcNow,
    ["identity"] = name,
    ["productionChanged"] = false,
    ["previewAuthorizationCreated"] = false,
    ["http503Disabled"] = false,
    ["persistentWfpFilters"] = 0,
    ["appContainerProfileCreated"] = false,
    ["administratorsEnabled"] = new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator),
};
nint sidPointer = 0;
var directoryCreated = false;
var profileCreated = false;
string? profileFolder = null;
var results = new Dictionary<string, ProcessResult>();
var counts = new Dictionary<string, Dictionary<string, long>>();
var failures = new Dictionary<string, object>();
var scriptPids = new Dictionary<string, List<uint>>();
try
{
    var derived = DeriveAppContainerSidFromAppContainerName(name, out sidPointer);
    if (derived != 0) Marshal.ThrowExceptionForHR(derived);
    var sid = new SecurityIdentifier(sidPointer);
    report["sid"] = sid.Value;
    report["capabilitiesGranted"] = Array.Empty<string>();
    report["wfp"] = ProbeWfp();
    var created = CreateAppContainerProfile(name, "AIPlatform temporary confinement probe",
        "Disposable per-run identity; no network capabilities", 0, 0, out var profileSid);
    if (created != 0) Marshal.ThrowExceptionForHR(created);
    profileCreated = true;
    report["appContainerProfileCreated"] = true;
    FreeSid(sidPointer);
    sidPointer = profileSid;
    var folderStatus = GetAppContainerFolderPath(sid.Value, out var folder);
    if (folderStatus != 0) Marshal.ThrowExceptionForHR(folderStatus);
    try { profileFolder = Marshal.PtrToStringUni(folder); }
    finally { Marshal.FreeCoTaskMem(folder); }
    if (Directory.Exists(root) || File.Exists(root))
        throw new IOException("Refusing to reuse an existing prototype directory.");
    Directory.CreateDirectory(root);
    directoryCreated = true;
    if ((File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0)
        throw new IOException("Refusing a reparse point as the prototype root.");
    var access = new DirectoryInfo(root).GetAccessControl();
    access.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl,
        InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
    new DirectoryInfo(root).SetAccessControl(access);
    LowerOwnedDirectoryIntegrity(root);
    var node = Path.Combine(root, "node.exe");
    var script = Path.Combine(root, "network-probe.mjs");
    File.Copy(args[1], node, overwrite: false);
    File.Copy(Path.Combine(AppContext.BaseDirectory, "network-probe.mjs"), script, overwrite: false);
    report["assets"] = new { nodeLength = new FileInfo(node).Length, scriptLength = new FileInfo(script).Length };
    results["asset-check"] = await RestrictedProcess.RunAsync(args[1], ["--check", script], root, "asset-check", 0, TimeSpan.FromSeconds(5));
    results["asset-access"] = await RestrictedProcess.RunAsync(args[1],
        ["--input-type=module", "-e", "import fs from 'node:fs'; try { console.log(JSON.stringify({size:fs.statSync(process.argv[1]).size,cwd:process.cwd()})) } catch(e) { console.error(e.code); process.exitCode=1 }", script],
        root, "asset-access", 0, TimeSpan.FromSeconds(5));
    await using (var lab = new NetworkLab())
    {
        var config = Path.Combine(root, "config.json");
        await File.WriteAllTextAsync(config, JsonSerializer.Serialize(new
        {
            directory = root, http = lab.Http, other = lab.Other, ipv6 = lab.IPv6, stun = lab.Stun, dns = lab.Dns
        }));
        foreach (var role in new[] { "control-before", "appcontainer", "control-after" })
        {
            lab.Phase = role;
            try
            {
                results[role] = await RestrictedProcess.RunAsync(role == "appcontainer" ? node : args[1],
                    ["--preserve-symlinks", "--preserve-symlinks-main", script, config, role], root, role,
                    role == "appcontainer" ? sidPointer : 0, TimeSpan.FromSeconds(10));
            }
            catch (Win32Exception exception) { failures[role] = new { nativeError = exception.NativeErrorCode, exception.Message }; }
            catch (IOException exception) { failures[role] = new { exception.Message }; }
            await Task.Delay(250);
            counts[role] = lab.Snapshot(role);
            var diagnostics = new List<JsonElement>();
            scriptPids[role] = [];
            foreach (var generation in new[] { "parent", "child" })
            {
                var file = Path.Combine(root, role + "-" + generation + ".json");
                if (File.Exists(file))
                {
                    using var data = JsonDocument.Parse(await File.ReadAllTextAsync(file));
                    diagnostics.Add(data.RootElement.Clone());
                    scriptPids[role].Add(data.RootElement.GetProperty("pid").GetUInt32());
                }
            }
            report[role + "-attempts"] = diagnostics;
        }
        var nativeExecuted = results.TryGetValue("appcontainer", out var native) && native.ExitCode == 0 &&
            !native.TimedOut && native.JobEmpty && native.Tokens.Count >= 2 &&
            native.Tokens.All(token => token.AppContainer && token.Sid == sid.Value && token.Error is null);
        nativeExecuted = nativeExecuted && scriptPids["appcontainer"].Count == 2 &&
            scriptPids["appcontainer"].All(pid => native!.Tokens.Any(token => token.Pid == pid));
        var nativeLeak = counts["appcontainer"].Any(pair => pair.Key.EndsWith("-connections") ||
            pair.Key.EndsWith("-packets")) && counts["appcontainer"].Values.Any(count => count > 0);
        report["nativeProbeVerified"] = nativeExecuted;
        if (!nativeExecuted) failures["native-child"] = new { reason = "Child execution under the dedicated token was not verified; inspect recorded spawn errors." };
        report["nativeNetworkLeak"] = nativeLeak;
        var parentExecuted = scriptPids["appcontainer"].Count >= 1 && native is not null &&
            native.Tokens.Any(token => token.Pid == scriptPids["appcontainer"][0] &&
                token.AppContainer && token.Sid == sid.Value && token.Error is null);
        report["nativeParentExecuted"] = parentExecuted;
        report["nativeParentNetworkDenied"] = parentExecuted && !nativeLeak;
        try { report["crashJob"] = await CrashJobProbe.RunAsync(node, root, sid.Value); }
        catch (Exception exception) when (exception is Win32Exception or IOException or OperationCanceledException or InvalidOperationException)
        { failures["crash-job"] = new { type = exception.GetType().Name, exception.Message }; }
        if (parentExecuted && !nativeLeak)
        {
            var edge = new[]
            {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Microsoft", "Edge", "Application", "msedge.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Microsoft", "Edge", "Application", "msedge.exe")
            }.FirstOrDefault(File.Exists);
            if (edge is null) failures["edge"] = new { reason = "Installed Edge not found; no browser download attempted." };
            else
            {
                report["edgeVersion"] = System.Diagnostics.FileVersionInfo.GetVersionInfo(edge).FileVersion;
                var page = Path.Combine(root, "network-test.html");
                await File.WriteAllTextAsync(page, $$"""
                    <!doctype html><html><body><pre id="result">NOT_EXECUTED</pre><script>
                    document.getElementById('result').textContent='JS_STARTED';
                    const urls=['http://127.0.0.1:{{lab.Http}}/edge','http://localhost:{{lab.Http}}/edge',
                      'http://[::1]:{{lab.IPv6}}/edge','http://127.0.0.1:{{lab.Other}}/edge'];
                    urls.forEach(url=>fetch(url,{mode:'no-cors'}).catch(()=>{}));
                    try {new WebSocket('ws://127.0.0.1:{{lab.Http}}/edge')} catch {}
                    const frame=document.createElement('iframe');frame.src='http://127.0.0.1:{{lab.Http}}/edge/redirect';document.body.append(frame);
                    try {
                      const pc=new RTCPeerConnection({iceServers:[{urls:'stun:127.0.0.1:{{lab.Stun}}'}]});
                      pc.createDataChannel('test');pc.createOffer().then(o=>pc.setLocalDescription(o)).catch(()=>{});
                    } catch(e) {document.body.append(e.name)}
                    setTimeout(()=>document.getElementById('result').textContent='JS_FINISHED',2500);
                    </script></body></html>
                    """);
                foreach (var role in new[] { "edge-control", "edge-appcontainer" })
                {
                    lab.Phase = role;
                    var profile = Path.Combine(root, role + "-profile");
                    try
                    {
                        results[role] = await RestrictedProcess.RunAsync(edge,
                            ["--headless", "--disable-extensions", "--disable-background-networking", "--disable-sync",
                                "--no-first-run", "--no-default-browser-check", "--enable-automation", "--dump-dom",
                                "--virtual-time-budget=6000", "--user-data-dir=" + profile, new Uri(page).AbsoluteUri],
                            root, role, role == "edge-appcontainer" ? sidPointer : 0, TimeSpan.FromSeconds(15));
                    }
                    catch (Win32Exception exception) { failures[role] = new { nativeError = exception.NativeErrorCode, exception.Message }; }
                    catch (IOException exception) { failures[role] = new { exception.Message }; }
                    await Task.Delay(250);
                    counts[role] = lab.Snapshot(role);
                    if (results.TryGetValue(role, out var browser))
                    {
                        report[role + "-javascriptExecuted"] = browser.Output.Contains("<pre id=\"result\">JS_FINISHED</pre>", StringComparison.Ordinal);
                        if (browser.ExitCode != 0 || browser.TimedOut)
                            failures[role] = new { exitCode = browser.ExitCode, timeout = browser.TimedOut };
                    }
                }
            }
        }
        else failures["edge"] = new { reason = "Edge not launched because native parent execution/network prerequisites failed." };
    }
    report["processes"] = results;
    report["counters"] = counts;
    report["failures"] = failures;
    report["integrationApproved"] = false;
    report["mainControllerCrashProfileRecoveryVerified"] = false;
}
catch (Exception exception) when (exception is Win32Exception or IOException or UnauthorizedAccessException or COMException)
{
    report["prototypeFailure"] = new { type = exception.GetType().Name, exception.Message };
}
finally
{
    if (sidPointer != 0) FreeSid(sidPointer);
    if (profileCreated)
    {
        var deleted = DeleteAppContainerProfile(name);
        report["appContainerProfileDeleteResult"] = deleted;
        report["appContainerProfileRemoved"] = deleted == 0 && profileFolder is not null && !Directory.Exists(profileFolder);
        if (deleted != 0) report["retainedProfileIdentity"] = name;
    }
    try
    {
        if (directoryCreated)
        {
            RemoveOwnedDirectory(root);
        }
        report["temporaryDirectoryRemoved"] = !Directory.Exists(root);
    }
    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
    {
        report["temporaryDirectoryRemoved"] = false;
        report["cleanupFailure"] = exception.Message;
        report["retainedDirectory"] = root;
    }
    report["processes"] = results;
    report["counters"] = counts;
    report["failures"] = failures;
    var json = JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true });
    await using var output = new FileStream(reportPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
    await using var writer = new StreamWriter(output);
    await writer.WriteAsync(json);
    Console.WriteLine(json);
}

static void RemoveOwnedDirectory(string directory)
{
    if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
        throw new IOException("Refusing to traverse a reparse point.");
    foreach (var child in Directory.EnumerateFileSystemEntries(directory))
    {
        var attributes = File.GetAttributes(child);
        if ((attributes & FileAttributes.ReparsePoint) != 0)
        {
            if ((attributes & FileAttributes.Directory) != 0) Directory.Delete(child, recursive: false);
            else File.Delete(child);
        }
        else if ((attributes & FileAttributes.Directory) != 0) RemoveOwnedDirectory(child);
        else
        {
            if ((attributes & FileAttributes.ReadOnly) != 0) File.SetAttributes(child, attributes & ~FileAttributes.ReadOnly);
            File.Delete(child);
        }
    }
    Directory.Delete(directory, recursive: false);
}
