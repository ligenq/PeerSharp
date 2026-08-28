using System.Diagnostics;
using System.Runtime.InteropServices;

namespace PeerSharp.EndToEnd;

internal sealed class ProcessSampler(Process process)
{
    private TimeSpan _baselineCpu;
    private IoCounters _baselineIo;
    private TimeSpan _lastCpu;
    private IoCounters _lastIo;

    public long PeakWorkingSetBytes { get; private set; }
    public long PeakPrivateBytes { get; private set; }

    public void Start()
    {
        Refresh();
        _baselineCpu = _lastCpu;
        _baselineIo = _lastIo;
        PeakWorkingSetBytes = 0;
        PeakPrivateBytes = 0;
    }

    public void Sample()
    {
        try
        {
            process.Refresh();
            PeakWorkingSetBytes = Math.Max(PeakWorkingSetBytes, process.WorkingSet64);
            PeakPrivateBytes = Math.Max(PeakPrivateBytes, process.PrivateMemorySize64);
            _lastCpu = process.TotalProcessorTime;
            _lastIo = ReadIoCounters(process);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
        }
    }

    public ProcessMetrics Finish(double wallSeconds)
    {
        Sample();
        double cpuSeconds = Math.Max(0, (_lastCpu - _baselineCpu).TotalSeconds);
        return new ProcessMetrics(
            cpuSeconds,
            wallSeconds > 0 ? cpuSeconds / wallSeconds * 100 : 0,
            PeakWorkingSetBytes,
            PeakPrivateBytes,
            CounterDelta(_lastIo.ReadTransferCount, _baselineIo.ReadTransferCount),
            CounterDelta(_lastIo.WriteTransferCount, _baselineIo.WriteTransferCount));
    }

    private void Refresh()
    {
        process.Refresh();
        _lastCpu = process.TotalProcessorTime;
        _lastIo = ReadIoCounters(process);
    }

    private static long CounterDelta(ulong current, ulong baseline)
        => current >= baseline && current - baseline <= long.MaxValue ? (long)(current - baseline) : 0;

    private static IoCounters ReadIoCounters(Process target)
    {
        if (!OperatingSystem.IsWindows()) return default;
        return GetProcessIoCounters(target.Handle, out IoCounters counters) ? counters : default;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProcessIoCounters(IntPtr processHandle, out IoCounters counters);

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }
}

internal sealed record ProcessMetrics(
    double CpuSeconds,
    double CpuPercentOneCore,
    long PeakWorkingSetBytes,
    long PeakPrivateBytes,
    long ReadBytes,
    long WriteBytes);
