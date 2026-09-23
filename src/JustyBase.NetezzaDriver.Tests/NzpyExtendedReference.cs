using System.Diagnostics;
using System.Text.Json;

namespace JustyBase.NetezzaDriver.Tests;

internal static class NzpyExtendedReference
{
    private sealed record ReferenceResponse(ReferenceResultSet[] ResultSets);

    internal sealed record ReferenceResultSet(string[] Columns, string?[][] Rows);

    internal static async Task<ReferenceResultSet[]> ExecuteAsync(
        IReadOnlyCollection<string> queries,
        CancellationToken cancellationToken = default)
    {
        string python = Environment.GetEnvironmentVariable("NZPY_EXTENDED_PYTHON")
            ?? (OperatingSystem.IsWindows() ? "python" : "python3");
        string scriptPath = Path.Combine(AppContext.BaseDirectory, "nzpy-extended-reference.py");

        if (!File.Exists(scriptPath))
        {
            throw new FileNotFoundException("The nzpy-extended reference script was not copied to the test output directory.", scriptPath);
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = python,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add(scriptPath);

        using var process = new Process { StartInfo = startInfo };
        try
        {
            process.Start();
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            throw new InvalidOperationException(
                $"Could not start Python executable '{python}'. Install nzpy-extended and set NZPY_EXTENDED_PYTHON if needed.",
                exception);
        }

        Task<string> standardOutputTask = process.StandardOutput.ReadToEndAsync();
        Task<string> standardErrorTask = process.StandardError.ReadToEndAsync();
        try
        {
            await process.StandardInput.WriteAsync(JsonSerializer.Serialize(new { queries }).AsMemory(), cancellationToken);
            process.StandardInput.Close();
            await process.WaitForExitAsync(cancellationToken);
        }
        catch
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(CancellationToken.None);
            }

            throw;
        }

        string standardOutput = await standardOutputTask;
        string standardError = await standardErrorTask;

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"nzpy-extended reference execution failed with exit code {process.ExitCode}: {standardError}");
        }

        var response = JsonSerializer.Deserialize<ReferenceResponse>(standardOutput, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        });

        return response?.ResultSets
            ?? throw new InvalidOperationException("nzpy-extended returned an empty or invalid result document.");
    }
}
