using System.Diagnostics;
using System.Text;

namespace EnvSync.Infrastructure.Tests;

/// <summary>
/// Runs emitted scripts in the <em>real</em> shells and reads back what each variable ended up holding,
/// so the tests prove the round trip instead of trusting the escaping rules on paper.
/// </summary>
internal static class ShellHarness
{
    private static readonly TimeSpan MaxRuntime = TimeSpan.FromSeconds(90);

    public static string? FindBash()
    {
        if (!OperatingSystem.IsWindows())
        {
            return FindOnPath("bash");
        }

        // Never System32\bash.exe: that is the WSL launcher, a different environment with different path rules.
        foreach (var root in new[] { Environment.GetEnvironmentVariable("ProgramFiles"), Environment.GetEnvironmentVariable("ProgramFiles(x86)") })
        {
            if (root is not null && File.Exists(Path.Combine(root, "Git", "bin", "bash.exe")))
            {
                return Path.Combine(root, "Git", "bin", "bash.exe");
            }
        }

        return null;
    }

    public static string? FindPwsh()
    {
        var onPath = FindOnPath(OperatingSystem.IsWindows() ? "pwsh.exe" : "pwsh");
        if (onPath is not null || !OperatingSystem.IsWindows())
        {
            return onPath;
        }

        var installed = Path.Combine(Environment.GetEnvironmentVariable("ProgramFiles") ?? string.Empty, "PowerShell", "7", "pwsh.exe");
        return File.Exists(installed) ? installed : null;
    }

    public static string? FindWindowsPowerShell()
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        var path = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
        return File.Exists(path) ? path : null;
    }

    /// <summary>Feeds <paramref name="script"/> to bash on stdin, then prints each variable followed by a NUL.</summary>
    public static async Task<IReadOnlyList<string>> RunBashAsync(
        string bash, string script, IReadOnlyList<string> variables, string workingDirectory, CancellationToken cancellationToken)
    {
        var names = string.Join(' ', variables.Select(v => $"\"${{{v}}}\""));
        var input = $"{script}printf '%s\\0' {names}\n";

        var result = await RunAsync(
            bash,
            ["--noprofile", "--norc", "-s"],
            rawArguments: null,
            Encoding.UTF8.GetBytes(input),
            workingDirectory,
            cancellationToken);

        return SplitOnNul(result);
    }

    /// <summary>
    /// Pipes the script into <c>Invoke-Expression</c> one line at a time, exactly as
    /// <c>envsync env --shell pwsh | Invoke-Expression</c> does, then reads the variables back as UTF-8.
    /// </summary>
    public static async Task<IReadOnlyList<string>> RunPowerShellAsync(
        string powershell, string script, IReadOnlyList<string> variables, string workingDirectory, CancellationToken cancellationToken)
    {
        var scriptBase64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(script));
        var quotedNames = string.Join(',', variables.Select(v => $"'{v}'"));

        var harness = $$"""
            $ErrorActionPreference = 'Stop'
            $script = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('{{scriptBase64}}'))
            $script -split "`n" | Where-Object { $_ -ne '' } | Invoke-Expression
            $stdout = [Console]::OpenStandardOutput()
            foreach ($name in @({{quotedNames}})) {
              $value = [Environment]::GetEnvironmentVariable($name)
              if ($null -eq $value) { $value = '' }
              $bytes = [Text.Encoding]::UTF8.GetBytes($value + [string][char]0)
              $stdout.Write($bytes, 0, $bytes.Length)
            }
            $stdout.Flush()
            """;

        var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(harness));
        var result = await RunAsync(
            powershell,
            ["-NoProfile", "-NonInteractive", "-EncodedCommand", encoded],
            rawArguments: null,
            stdin: null,
            workingDirectory,
            cancellationToken);

        return SplitOnNul(result);
    }

    /// <summary>Calls the script from a real <c>.cmd</c> file and reads the variables back with <c>set</c>.</summary>
    public static async Task<IReadOnlyList<string>> RunCmdAsync(
        string script, string variablePrefix, int count, string scriptDirectory, string workingDirectory, CancellationToken cancellationToken)
    {
        var scriptPath = Path.Combine(scriptDirectory, "run.cmd");
        await File.WriteAllTextAsync(scriptPath, "@echo off\r\n" + script, Encoding.ASCII, cancellationToken);

        var result = await RunAsync(
            "cmd.exe",
            argumentList: null,
            rawArguments: $"/d /s /c \"call \"{scriptPath}\" && set {variablePrefix}\"",
            stdin: null,
            workingDirectory,
            cancellationToken);

        var values = new string[count];
        foreach (var line in Encoding.Latin1.GetString(result).Split("\r\n", StringSplitOptions.RemoveEmptyEntries))
        {
            var equals = line.IndexOf('=', StringComparison.Ordinal);
            if (line.StartsWith(variablePrefix, StringComparison.Ordinal)
                && equals > variablePrefix.Length
                && int.TryParse(line.AsSpan(variablePrefix.Length, equals - variablePrefix.Length), out var index)
                && index < count)
            {
                values[index] = line[(equals + 1)..];
            }
        }

        return [.. values.Select(v => v ?? string.Empty)];
    }

    /// <summary>
    /// The path <c>envsync env | Invoke-Expression</c> really takes: the script's bytes come out of a native process's stdout, are
    /// decoded by PowerShell with the console's own code page (left at its default), and only then evaluated line by line.
    /// </summary>
    public static async Task<IReadOnlyList<string>> RunPowerShellThroughANativePipeAsync(
        string powershell,
        string childExecutable,
        string scriptFile,
        string reportFile,
        IReadOnlyList<string> variables,
        string workingDirectory,
        CancellationToken cancellationToken)
    {
        var quotedNames = string.Join(',', variables.Select(v => $"'{v}'"));

        var harness = $$"""
            $ErrorActionPreference = 'Stop'
            & '{{childExecutable}}' '{{reportFile}}' 'cat={{scriptFile}}' | Invoke-Expression
            $stdout = [Console]::OpenStandardOutput()
            foreach ($name in @({{quotedNames}})) {
              $value = [Environment]::GetEnvironmentVariable($name)
              if ($null -eq $value) { $value = '' }
              $bytes = [Text.Encoding]::UTF8.GetBytes($value + [string][char]0)
              $stdout.Write($bytes, 0, $bytes.Length)
            }
            $stdout.Flush()
            """;

        var result = await RunAsync(
            powershell,
            ["-NoProfile", "-NonInteractive", "-EncodedCommand", Convert.ToBase64String(Encoding.Unicode.GetBytes(harness))],
            rawArguments: null,
            stdin: null,
            workingDirectory,
            cancellationToken);

        return SplitOnNul(result);
    }

    /// <summary>Runs a whole script with <c>set -e</c> semantics available to it, then reports how the shell ended.</summary>
    public static async Task<(int ExitCode, string Stdout)> RunBashScriptAsync(
        string bash, string script, string workingDirectory, CancellationToken cancellationToken)
    {
        var result = await RunRawAsync(
            bash,
            ["--noprofile", "--norc", "-s"],
            rawArguments: null,
            Encoding.UTF8.GetBytes(script),
            workingDirectory,
            cancellationToken);

        return (result.ExitCode, Encoding.UTF8.GetString(result.Stdout));
    }

    /// <summary>Pipes the script into <c>Invoke-Expression</c> line by line with errors terminating, then prints AFTER if it got that far.</summary>
    public static async Task<(int ExitCode, string Stdout)> RunPowerShellScriptAsync(
        string powershell, string script, string workingDirectory, CancellationToken cancellationToken)
    {
        var harness = $$"""
            $ErrorActionPreference = 'Stop'
            $script = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('{{Convert.ToBase64String(Encoding.UTF8.GetBytes(script))}}'))
            $script -split "`n" | Where-Object { $_ -ne '' } | Invoke-Expression
            [Console]::Out.Write('AFTER')
            """;

        var result = await RunRawAsync(
            powershell,
            ["-NoProfile", "-NonInteractive", "-EncodedCommand", Convert.ToBase64String(Encoding.Unicode.GetBytes(harness))],
            rawArguments: null,
            stdin: null,
            workingDirectory,
            cancellationToken);

        return (result.ExitCode, Encoding.UTF8.GetString(result.Stdout));
    }

    /// <summary>Calls the script from a real <c>.cmd</c> file, then prints FAILED_WITH_11 if the error level ended up at 11 or more.</summary>
    public static async Task<(int ExitCode, string Stdout)> RunCmdScriptAsync(
        string script, string scriptDirectory, string workingDirectory, CancellationToken cancellationToken)
    {
        var scriptPath = Path.Combine(scriptDirectory, "run.cmd");
        await File.WriteAllTextAsync(scriptPath, "@echo off\r\n" + script, Encoding.ASCII, cancellationToken);

        var result = await RunRawAsync(
            "cmd.exe",
            argumentList: null,
            rawArguments: $"/d /s /c \"call \"{scriptPath}\" & if errorlevel 11 echo FAILED_WITH_11\"",
            stdin: null,
            workingDirectory,
            cancellationToken);

        return (result.ExitCode, Encoding.Latin1.GetString(result.Stdout));
    }

    private static List<string> SplitOnNul(byte[] stdout)
    {
        var parts = Encoding.UTF8.GetString(stdout).Split('\0').ToList();
        if (parts.Count > 0 && parts[^1].Length == 0)
        {
            parts.RemoveAt(parts.Count - 1);
        }

        return parts;
    }

    private static async Task<byte[]> RunAsync(
        string fileName,
        string[]? argumentList,
        string? rawArguments,
        byte[]? stdin,
        string workingDirectory,
        CancellationToken cancellationToken)
    {
        var result = await RunRawAsync(fileName, argumentList, rawArguments, stdin, workingDirectory, cancellationToken);
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException($"{Path.GetFileName(fileName)} exited with {result.ExitCode}. stderr: {result.Stderr}");
        }

        return result.Stdout;
    }

    private static async Task<(int ExitCode, byte[] Stdout, string Stderr)> RunRawAsync(
        string fileName,
        string[]? argumentList,
        string? rawArguments,
        byte[]? stdin,
        string workingDirectory,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo(fileName)
        {
            RedirectStandardInput = stdin is not null,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = workingDirectory,
        };

        if (rawArguments is not null)
        {
            startInfo.Arguments = rawArguments;
        }
        else
        {
            foreach (var argument in argumentList ?? [])
            {
                startInfo.ArgumentList.Add(argument);
            }
        }

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException($"Could not start {fileName}.");
        var stdout = ReadAllAsync(process.StandardOutput.BaseStream, cancellationToken);
        var stderr = process.StandardError.ReadToEndAsync(cancellationToken);

        if (stdin is not null)
        {
            await process.StandardInput.BaseStream.WriteAsync(stdin, cancellationToken);
            process.StandardInput.Close();
        }

        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(MaxRuntime);
        try
        {
            await process.WaitForExitAsync(budget.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException($"{Path.GetFileName(fileName)} did not finish within {MaxRuntime.TotalSeconds:0}s.");
        }

        return (process.ExitCode, await stdout, await stderr);
    }

    private static async Task<byte[]> ReadAllAsync(Stream stream, CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer, cancellationToken);
        return buffer.ToArray();
    }

    private static string? FindOnPath(string fileName)
    {
        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = Path.Combine(directory, fileName);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }
}
