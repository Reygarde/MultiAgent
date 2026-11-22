using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using AIPlatform.Core.Studio;

namespace AIPlatform.Infrastructure.Studio;

public sealed record ReactSourceIssue(string File, int Line, int Column, string Code, string Message);

public interface IReactSourceCheck
{
    Task<IReadOnlyList<ReactSourceIssue>> CheckAsync(IReadOnlyList<GeneratedFile> files, CancellationToken token);
}
public sealed record ReactSourceEdit(string File, int Start, int End, string Before, string After, string Code,
    string OriginalSha256, string CandidateSha256);
public sealed record ReactRepairRefusal(string File, string Reason);
public sealed record ReactSourceRepairResult(IReadOnlyList<GeneratedFile> Files,
    IReadOnlyList<ReactSourceIssue> Before, IReadOnlyList<ReactSourceIssue> Issues,
    IReadOnlyList<ReactSourceEdit> Edits, IReadOnlyList<ReactRepairRefusal> Refusals);
public interface IReactSourceRepair
{
    Task<ReactSourceRepairResult> RepairAsync(IReadOnlyList<GeneratedFile> files, IReadOnlyList<string> allowedPaths, CancellationToken token);
}

public sealed class ReactSourceCheck(string node, string helper) : IReactSourceCheck, IReactSourceRepair
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<IReadOnlyList<ReactSourceIssue>> CheckAsync(IReadOnlyList<GeneratedFile> files, CancellationToken token) =>
        await RunAsync<ReactSourceIssue[]>(new { files, templateId = MissionReactTemplate.Id }, helper, token);

    public Task<ReactSourceRepairResult> RepairAsync(IReadOnlyList<GeneratedFile> files, IReadOnlyList<string> allowedPaths, CancellationToken token)
    {
        if (allowedPaths.Any(path => !MissionReactTemplate.IsEditable(path) || files.All(file => file.Path != path)))
            throw new StudioValidationException("AST repair targets must be existing editable React sources.");
        MissionReactTemplate.Validate(files);
        return RunAsync<ReactSourceRepairResult>(new { files, allowedPaths, templateId = MissionReactTemplate.Id,
            trustedSetup = MissionReactTemplate.Files.Single(file => file.Path == "test-setup.ts").Content },
            Path.Combine(Path.GetDirectoryName(helper)!, "react-source-repair.mjs"), token);
    }

    private async Task<T> RunAsync<T>(object input, string script, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!Path.IsPathFullyQualified(node) || !Path.IsPathFullyQualified(script) ||
            !File.Exists(node) || !File.Exists(script))
            throw new StudioValidationException("Trusted React source checker or Node.js is unavailable; publication is blocked.");
        SafePaths.RejectLinks(script);
        using var process = new Process { StartInfo = new(node) {
            UseShellExecute = false, CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(script)!,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false), StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            ArgumentList = { "--max-old-space-size=256", script, "--stdio" }
        } };
        foreach (var key in new[] { "NODE_OPTIONS", "NODE_PATH", "NODE_V8_COVERAGE", "NODE_COMPILE_CACHE" })
            process.StartInfo.Environment.Remove(key);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        try { process.Start(); }
        catch (Win32Exception)
        {
            throw new StudioValidationException("Trusted React source checker could not start; publication is blocked.");
        }
        var output = process.StandardOutput.ReadToEndAsync(timeout.Token);
        var errors = process.StandardError.ReadToEndAsync(timeout.Token);
        try
        {
            await process.StandardInput.WriteAsync(JsonSerializer.Serialize(input, Json).AsMemory(), timeout.Token);
            process.StandardInput.Close();
            await process.WaitForExitAsync(timeout.Token);
            if (process.ExitCode != 0)
            {
                var error = await errors;
                var summary = error.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                    .FirstOrDefault(line => line.StartsWith("Error [", StringComparison.Ordinal) ||
                        line.StartsWith("Error:", StringComparison.Ordinal) || line.StartsWith("TypeError:", StringComparison.Ordinal))?.Trim();
                throw new StudioValidationException($"Trusted React source checker failed (exit {process.ExitCode}" +
                    $"{(summary is null ? "" : $": {summary}")}); publication is blocked.");
            }
            return JsonSerializer.Deserialize<T>(await output, Json)
                ?? throw new StudioValidationException("React source checker returned no result.");
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            throw new StudioValidationException("Trusted React source check timed out; publication is blocked.");
        }
        catch (IOException)
        {
            throw new StudioValidationException("Trusted React source checker communication failed; publication is blocked.");
        }
        catch (JsonException)
        {
            throw new StudioValidationException("Trusted React source checker returned invalid diagnostics; publication is blocked.");
        }
        finally
        {
            if (!process.HasExited)
            {
                try { process.Kill(entireProcessTree: true); }
                catch (InvalidOperationException) when (process.HasExited) { }
                await process.WaitForExitAsync(CancellationToken.None);
            }
        }
    }

    public static string Describe(IEnumerable<ReactSourceIssue> issues) =>
        string.Join("\n", issues.Select(issue => $"{issue.File}({issue.Line},{issue.Column}): {issue.Code}: {issue.Message}"));
}
