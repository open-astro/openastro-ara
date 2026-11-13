#region "copyright"

/*
    Copyright (c) 2026 Open Astro and the OpenAstro Ara contributors

    This file is part of OpenAstro Ara (forked from N.I.N.A.).

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using Microsoft.Extensions.Hosting;
using OpenAstroAra.Server.Contracts;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Server.Services;

/// <summary>One raw read; a series that could not be read is null.</summary>
public readonly record struct UsageReading(long? StorageFree, long? StorageTotal, TimeSpan? CpuTime, long? MemoryBytes);

/// <summary>
/// #1582 — every 15 s records the save volume's free/total, the process's CPU as a share of ALL cores
/// and its working set into a fixed 12 h ring (2880 samples, oldest dropped). In memory only: a daemon
/// restart starts empty. The CPU baseline is this service's own — it never touches
/// <see cref="ServerInternalStateService"/>, so a Stats panel read and a tick cannot shorten each
/// other's CPU window.
/// </summary>
public sealed class UsageHistoryService : BackgroundService {

    public const int IntervalSeconds = 15;
    public const int Capacity = 12 * 3600 / IntervalSeconds;

    private readonly Func<UsageReading> _read;
    private readonly Func<DateTimeOffset> _clock;
    private readonly int _processors;
    private readonly object _gate = new();
    private readonly Queue<UsageSampleDto> _ring = new();
    private TimeSpan? _lastCpu;
    private DateTimeOffset _lastAt;

    /// <param name="read">Test seam for the sample source.</param>
    /// <param name="clock">Test seam for "now".</param>
    /// <param name="processors">Test seam for the core count.</param>
    public UsageHistoryService(Func<UsageReading> read, Func<DateTimeOffset>? clock = null, int? processors = null) {
        _read = read;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
        _processors = Math.Max(1, processors ?? Environment.ProcessorCount);
    }

    /// <summary>The production source: the save directory's volume (as GET /storage/space resolves it)
    /// and this process's CPU time and working set.</summary>
    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "A failed read stores a gap for that series; it must never stop the sampler.")]
    public static Func<UsageReading> SystemReader(Func<string?> saveDirectory) => () => {
        long? free = null, total = null, mem = null;
        TimeSpan? cpu = null;
        try {
            var configured = saveDirectory();
            var dir = string.IsNullOrWhiteSpace(configured) ? Path.Combine(AppContext.BaseDirectory, "frames") : configured;
            if (Directory.Exists(dir) && DiskSpaceMonitor.TryGetSpace(dir) is { } space) {
                (free, total) = (space.Free, space.Total);
            }
        } catch (Exception) {
            // gap
        }
        try {
            using var process = Process.GetCurrentProcess();
            cpu = process.TotalProcessorTime;
            mem = process.WorkingSet64;
        } catch (Exception) {
            // gap
        }
        return new UsageReading(free, total, cpu, mem);
    };

    /// <summary>Take one sample now. CPU is null for the first tick and after a failed CPU read.</summary>
    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "A throwing source is a gap in every series, not a dead sampler.")]
    public void Tick() {
        UsageReading r;
        try {
            r = _read();
        } catch (Exception) {
            r = default;
        }
        lock (_gate) {
            var now = _clock();
            double? cpuPercent = null;
            if (r.CpuTime is { } cpu && _lastCpu is { } last && (now - _lastAt).TotalMilliseconds > 0) {
                cpuPercent = Math.Clamp((cpu - last).TotalMilliseconds / (now - _lastAt).TotalMilliseconds * 100.0 / _processors, 0, 100);
            }
            _lastCpu = r.CpuTime;
            _lastAt = now;
            if (_ring.Count >= Capacity) {
                _ring.Dequeue();
            }
            _ring.Enqueue(new UsageSampleDto(now, r.StorageFree, r.StorageTotal, cpuPercent, r.MemoryBytes));
        }
    }

    /// <summary>The samples oldest first; with <paramref name="since"/> only those strictly newer.</summary>
    public UsageHistoryDto Snapshot(DateTimeOffset? since = null) {
        lock (_gate) {
            var samples = _ring.Where(s => since is null || s.AtUtc > since).ToList();
            return new UsageHistoryDto(IntervalSeconds, _processors, samples);
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken) {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(IntervalSeconds));
        Tick();
        try {
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false)) {
                Tick();
            }
        } catch (OperationCanceledException) {
            // shutting down
        }
    }
}
