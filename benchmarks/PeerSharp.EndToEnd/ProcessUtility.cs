using System.Diagnostics;
using System.Text;

namespace PeerSharp.EndToEnd;

internal sealed record ProcessOutput(int ExitCode, string StandardOutput, string StandardError)
{
    public string Combined => StandardOutput + StandardError;
}

internal static class ProcessUtility
{
    public static async Task<ProcessOutput> RunAsync(
        string executable,
        IEnumerable<string> arguments,
        string workingDirectory,
        CancellationToken cancellationToken,
        IReadOnlyDictionary<string, string?>? environment = null,
        TextWriter? progress = null)
    {
        var startInfo = CreateStartInfo(executable, arguments, workingDirectory, redirectInput: false, environment);
        progress?.WriteLine($"> {FormatCommand(startInfo)}");

        using var process = new Process { StartInfo = startInfo };
        if (!process.Start()) throw new InvalidOperationException($"Could not start {executable}.");

        Task<string> stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
        Task<string> stderr = process.StandardError.ReadToEndAsync(cancellationToken);
        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            TryKill(process);
            throw;
        }

        string standardOutput = await stdout.ConfigureAwait(false);
        string standardError = await stderr.ConfigureAwait(false);
        if (progress is not null)
        {
            if (!string.IsNullOrWhiteSpace(standardOutput)) progress.Write(standardOutput);
            if (!string.IsNullOrWhiteSpace(standardError)) progress.Write(standardError);
        }

        return new ProcessOutput(process.ExitCode, standardOutput, standardError);
    }

    public static ProcessStartInfo CreateStartInfo(
        string executable,
        IEnumerable<string> arguments,
        string workingDirectory,
        bool redirectInput,
        IReadOnlyDictionary<string, string?>? environment = null)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = redirectInput
        };
        foreach (string argument in arguments) startInfo.ArgumentList.Add(argument);
        if (environment is not null)
        {
            foreach ((string key, string? value) in environment) startInfo.Environment[key] = value;
        }

        return startInfo;
    }

    public static string FormatCommand(ProcessStartInfo startInfo)
    {
        var command = new StringBuilder(Quote(startInfo.FileName));
        foreach (string argument in startInfo.ArgumentList) command.Append(' ').Append(Quote(argument));
        return command.ToString();
    }

    public static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
        }
    }

    private static string Quote(string value)
        => value.Length != 0 && value.All(static c => !char.IsWhiteSpace(c) && c != '"')
            ? value
            : '"' + value.Replace("\"", "\\\"") + '"';
}

internal sealed class CapturedProcess : IAsyncDisposable
{
    private readonly Process _process;
    private readonly StreamWriter _log;
    private readonly object _logLock = new();
    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly string? _readyMarker;

    private CapturedProcess(Process process, StreamWriter log, string? readyMarker)
    {
        _process = process;
        _log = log;
        _readyMarker = readyMarker;
    }

    public Process Process => _process;
    public bool HasExited => _process.HasExited;
    public int? ExitCode => _process.HasExited ? _process.ExitCode : null;

    public static CapturedProcess Start(ProcessStartInfo startInfo, string logPath, string? readyMarker = null)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);
        var log = new StreamWriter(logPath, append: false, Encoding.UTF8) { AutoFlush = true };
        var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        var captured = new CapturedProcess(process, log, readyMarker);
        process.OutputDataReceived += captured.OnOutput;
        process.ErrorDataReceived += captured.OnOutput;
        process.Exited += captured.OnExited;
        log.WriteLine($"> {ProcessUtility.FormatCommand(startInfo)}");
        if (!process.Start())
        {
            log.Dispose();
            throw new InvalidOperationException($"Could not start {startInfo.FileName}.");
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        if (readyMarker is null) captured._ready.TrySetResult();
        return captured;
    }

    public async Task WaitUntilReadyAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout);
        await _ready.Task.WaitAsync(timeoutCts.Token).ConfigureAwait(false);
    }

    public Task WaitForExitAsync(CancellationToken cancellationToken)
        => _process.WaitForExitAsync(cancellationToken);

    public async Task StopAsync(TimeSpan gracePeriod)
    {
        if (_process.HasExited) return;
        try
        {
            await _process.StandardInput.WriteLineAsync("q").ConfigureAwait(false);
            await _process.StandardInput.FlushAsync().ConfigureAwait(false);
            using var grace = new CancellationTokenSource(gracePeriod);
            await _process.WaitForExitAsync(grace.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or OperationCanceledException)
        {
            ProcessUtility.TryKill(_process);
            await _process.WaitForExitAsync().ConfigureAwait(false);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        _process.CancelOutputRead();
        _process.CancelErrorRead();
        _process.Dispose();
        lock (_logLock) _log.Dispose();
    }

    private void OnOutput(object sender, DataReceivedEventArgs e)
    {
        if (e.Data is null) return;
        lock (_logLock) _log.WriteLine(e.Data);
        if (_readyMarker is not null && e.Data.Contains(_readyMarker, StringComparison.Ordinal))
        {
            _ready.TrySetResult();
        }
    }

    private void OnExited(object? sender, EventArgs e)
    {
        if (_readyMarker is not null && !_ready.Task.IsCompleted)
        {
            _ready.TrySetException(new InvalidOperationException(
                $"{Path.GetFileName(_process.StartInfo.FileName)} exited with code {_process.ExitCode} before becoming ready."));
        }
    }
}
