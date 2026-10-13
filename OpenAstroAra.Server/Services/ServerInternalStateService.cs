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

    public ServerInternalStateService(Func<DateTimeOffset>? clock = null) {
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
        _startedUtc = _clock();
    }

    public ServerInternalStateDto Sample() {
        var now = _clock();
        using var process = Process.GetCurrentProcess();
        var gc = GC.GetGCMemoryInfo();
        ThreadPool.GetAvailableThreads(out var workerAvailable, out _);
        ThreadPool.GetMaxThreads(out var workerMax, out _);
        var cpu = CpuPercent(process.TotalProcessorTime, now);
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
                ThreadCount: process.Threads.Count,
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
    internal double? CpuPercent(TimeSpan totalCpu, DateTimeOffset now) {
        lock (_cpuGate) {
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
            return result;
        }
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
