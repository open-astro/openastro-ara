#region "copyright"

/*
    Copyright (c) 2026 Open Astro and the OpenAstro Ara contributors

    This file is part of OpenAstro Ara (forked from N.I.N.A.).

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using System;
using System.Threading.Tasks;

namespace OpenAstroAra.Server.Services;

/// <summary>
/// #1219 — rate-limits the §28 checkpoint writes that ride progress reports. Every solver output
/// line reaches the run's progress sink (#1188), and each report used to serialize and move the
/// checkpoint file on the SD card; a chatty solver did hundreds of writes per centering. A poke
/// writes at once when the last write is older than <c>minInterval</c>, otherwise it arms ONE
/// trailing write for when the interval has passed, so a burst collapses to a leading and a
/// trailing write and the freshest state always lands. Lifecycle writes (start, pause, resume,
/// stop) bypass this and stay immediate. Thread-safe; <see cref="Flush"/> writes any pending
/// state now (the run's finally), <see cref="Stop"/> ends it. The trailing write is a delayed task
/// rather than a Timer field so the type owns nothing disposable: like
/// <see cref="CoalescingAsyncPublisher"/> it lives across a run's try/finally, where CA2000's
/// ownership analysis cannot follow a disposable.
/// </summary>
public sealed class CoalescingCheckpointWriter {

    private readonly Action _write;
    private readonly long _minIntervalMs;
    private readonly Func<long> _clockMs;
    private readonly object _gate = new();
    private long _lastWriteMs = long.MinValue / 2;
    private bool _pending;
    private bool _disposed;
    private int _generation; // a Flush/Stop makes an armed trailing write stale

    public CoalescingCheckpointWriter(Action write, TimeSpan minInterval, Func<long>? clockMs = null) {
        _write = write ?? throw new ArgumentNullException(nameof(write));
        _minIntervalMs = Math.Max(0, (long)minInterval.TotalMilliseconds);
        _clockMs = clockMs ?? (static () => Environment.TickCount64);
    }

    /// <summary>Checkpoint writes performed so far (tests).</summary>
    public int Writes { get; private set; }

    /// <summary>Something changed: write now if the interval has passed, else arm the trailing write.</summary>
    public void Poke() {
        lock (_gate) {
            if (_disposed) {
                return;
            }
            var now = _clockMs();
            var due = _lastWriteMs + _minIntervalMs;
            if (now >= due) {
                WriteLocked(now);
                return;
            }
            if (!_pending) {
                _pending = true;
                var generation = _generation;
                _ = Task.Delay(TimeSpan.FromMilliseconds(Math.Max(1, due - now)))
                    .ContinueWith(_ => OnTrailing(generation), TaskScheduler.Default);
            }
        }
    }

    /// <summary>Write any pending state now (the run is ending).</summary>
    public void Flush() {
        lock (_gate) {
            if (_disposed || !_pending) {
                return;
            }
            _generation++; // the armed trailing write is now stale
            WriteLocked(_clockMs());
        }
    }

    private void OnTrailing(int generation) {
        lock (_gate) {
            if (_disposed || !_pending || generation != _generation) {
                return;
            }
            WriteLocked(_clockMs());
        }
    }

    private void WriteLocked(long now) {
        _pending = false;
        _lastWriteMs = now;
        Writes++;
        _write();
    }

    /// <summary>End the writer; an armed trailing write and later pokes are ignored.</summary>
    public void Stop() {
        lock (_gate) {
            _disposed = true;
            _pending = false;
            _generation++;
        }
    }
}
