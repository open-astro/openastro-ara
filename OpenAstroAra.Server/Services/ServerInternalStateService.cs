#region "copyright"

/*
    Copyright © 2016 - 2024 Stefan Berg <isbeorn86+NINA@googlemail.com> and the N.I.N.A. contributors

    This file is part of N.I.N.A. - Nighttime Imaging 'N' Astronomy.

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using OpenAstroAra.Server.Contracts;
using OpenAstroAra.Server.Endpoints;
using System;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

namespace OpenAstroAra.Server.Services;

/// <summary>
/// §66.7 / #1175 — the daemon's live internal state for the Stats "Pi performance" panel:
/// process memory, GC, CPU, thread pool, open WebSocket clients, host load. Everything here
/// is read from the runtime in microseconds; nothing is sampled in the background, so a
/// panel nobody opens costs nothing. CPU is the share of wall time the process consumed
/// between two consecutive reads (the first read after boot reports null).
/// </summary>
public sealed class ServerInternalStateService {

    private readonly Func<DateTimeOffset> _clock;
    private readonly DateTimeOffset _startedUtc;
    private readonly object _cpuGate = new();
    private TimeSpan _lastCpu;
    private DateTimeOffset _lastCpuAt;
    private bool _haveCpuSample;

    /// <param name="clock">Test seam for "now".</param>
    /// <param name="startedUtc">Test seam for the daemon's start. Production leaves it null and the
    /// PROCESS start time is used (r2 finding): the singleton is built on the first panel read, which
    /// can be days after boot, so the service's own construction time is not the daemon's uptime.</param>
    public ServerInternalStateService(Func<DateTimeOffset>? clock = null, DateTimeOffset? startedUtc = null) {
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
        _startedUtc = startedUtc ?? ProcessStartUtc();
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "A platform that refuses the process start time falls back to 'now' (uptime counts from here) rather than failing the sample.")]
    private static DateTimeOffset ProcessStartUtc() {
        try {
            using var process = Process.GetCurrentProcess();
            return new DateTimeOffset(process.StartTime.ToUniversalTime());
        } catch (Exception) {
            return DateTimeOffset.UtcNow;
        }
    }

    public ServerInternalStateDto Sample() {
        using var process = Process.GetCurrentProcess();
        var gc = GC.GetGCMemoryInfo();
        ThreadPool.GetAvailableThreads(out var workerAvailable, out _);
        ThreadPool.GetMaxThreads(out var workerMax, out _);
        // Clock and CPU are read inside the gate (r1 note): two overlapping reads must not hand the
        // earlier one a later baseline and a negative window.
        var (cpu, now) = CpuPercent(() => (process.TotalProcessorTime, _clock()));
        return new ServerInternalStateDto(
            CapturedUtc: now,
            UptimeSeconds: Math.Max(0, (now - _startedUtc).TotalSeconds),
            Process: new ProcessStateDto(
                WorkingSetBytes: process.WorkingSet64,
                ManagedHeapBytes: GC.GetTotalMemory(forceFullCollection: false),
                HeapCommittedBytes: gc.TotalCommittedBytes,
                Gen0Collections: GC.CollectionCount(0),
                Gen1Collections: GC.CollectionCount(1),
                Gen2Collections: GC.CollectionCount(2),
                GcPauseTotalMs: GC.GetTotalPauseDuration().TotalMilliseconds,
                CpuPercent: cpu,
                ThreadCount: ThreadCount(process),
                ThreadPoolBusyWorkers: Math.Max(0, workerMax - workerAvailable),
                ThreadPoolPendingWorkItems: ThreadPool.PendingWorkItemCount),
            WebSockets: new WsStateDto(WsConnectionGate.Open, WsConnectionGate.MaxConcurrentConnections),
            Host: new HostStateDto(
                ProcessorCount: Environment.ProcessorCount,
                LoadAverage1Min: ReadLoadAverage(),
                OsDescription: RuntimeInformation.OSDescription,
                Architecture: RuntimeInformation.ProcessArchitecture.ToString(),
                RuntimeVersion: Environment.Version.ToString()));
    }

    /// <summary>Process CPU as a percentage of one core's wall time since the previous
    /// sample (so 400 % is four cores flat out). Null until a second sample exists.</summary>
    internal double? CpuPercent(TimeSpan totalCpu, DateTimeOffset now) => CpuPercent(() => (totalCpu, now)).Cpu;

    /// <summary>The locked form: <paramref name="read"/> runs inside the gate so the CPU time and the
    /// clock belong to the same sample and consecutive samples are strictly ordered.</summary>
    private (double? Cpu, DateTimeOffset Now) CpuPercent(Func<(TimeSpan Cpu, DateTimeOffset Now)> read) {
        lock (_cpuGate) {
            var (totalCpu, now) = read();
            double? result = null;
            if (_haveCpuSample) {
                var wall = (now - _lastCpuAt).TotalMilliseconds;
                if (wall > 0) {
                    result = Math.Max(0, (totalCpu - _lastCpu).TotalMilliseconds / wall * 100.0);
                }
            }
            _lastCpu = totalCpu;
            _lastCpuAt = now;
            _haveCpuSample = true;
            return (result, now);
        }
    }

    /// <summary>Thread count without materialising a ProcessThread per thread: on Linux the
    /// /proc/self/task entries are the threads (r1 note — a Pi daemon runs a few dozen).</summary>
    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "A cheaper read that falls back to the portable one on any fault.")]
    private static int ThreadCount(Process process) {
        try {
            if (Directory.Exists("/proc/self/task")) {
                return Directory.GetDirectories("/proc/self/task").Length;
            }
        } catch (Exception) {
            // fall through to the portable read
        }
        return process.Threads.Count;
    }

    /// <summary>Linux 1-minute load average from /proc/loadavg; null elsewhere or when unreadable.</summary>
    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "A best-effort host figure: any read or parse fault means 'unknown', never a failed sample.")]
    internal static double? ReadLoadAverage(string path = "/proc/loadavg") {
        try {
            if (!File.Exists(path)) {
                return null;
            }
            var first = File.ReadAllText(path).Split(' ', StringSplitOptions.RemoveEmptyEntries);
            return first.Length > 0 && double.TryParse(first[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : null;
        } catch (Exception) {
            return null;
        }
    }
}
