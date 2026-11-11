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
using System.Collections.Generic;

namespace OpenAstroAra.Server.Services;

/// <summary>#1649 — per-axis MoveAxis deadlines for a leased direction-pad press. The client renews
/// while the press lasts; an axis whose deadline passes is handed back once by
/// <see cref="TakeExpired"/> so the telescope service can stop it. A renewal at the rate the axis
/// already moves at only extends the deadline (<see cref="TryRenew"/>): drivers that queue motion
/// reject a MoveAxis repeated every 300 ms ("Motion superseded before dispatch", EQMOD through
/// AlpacaBridge, 2026-11-11). Thread-safe.</summary>
public sealed class MoveAxisLease {

    public const int MinMs = 250;
    public const int MaxMs = 5000;

    private readonly object _gate = new();
    private readonly Dictionary<int, (DateTimeOffset Deadline, double Rate)> _deadlines = [];

    public static int Clamp(int leaseMs) => Math.Clamp(leaseMs, MinMs, MaxMs);

    public bool Any {
        get {
            lock (_gate) {
                return _deadlines.Count > 0;
            }
        }
    }

    /// <summary>Starts or renews <paramref name="axis"/>'s deadline for a move at <paramref name="rate"/>.</summary>
    public void Renew(int axis, double rate, int leaseMs, DateTimeOffset now) {
        lock (_gate) {
            _deadlines[axis] = (now.AddMilliseconds(Clamp(leaseMs)), rate);
        }
    }

    /// <summary>True, with the deadline extended, when <paramref name="axis"/> holds a live lease at
    /// <paramref name="rate"/>: the mount already moves so, and the driver need not be asked again.</summary>
    public bool TryRenew(int axis, double rate, int leaseMs, DateTimeOffset now) {
        lock (_gate) {
            if (!_deadlines.TryGetValue(axis, out var held) || held.Rate != rate || held.Deadline <= now) {
                return false;
            }
            _deadlines[axis] = (now.AddMilliseconds(Clamp(leaseMs)), rate);
            return true;
        }
    }

    public void Cancel(int axis) {
        lock (_gate) {
            _deadlines.Remove(axis);
        }
    }

    public void CancelAll() {
        lock (_gate) {
            _deadlines.Clear();
        }
    }

    /// <summary>The axes whose deadline is at or before <paramref name="now"/>, removed as they are returned.</summary>
    public IReadOnlyList<int> TakeExpired(DateTimeOffset now) {
        lock (_gate) {
            var expired = new List<int>();
            foreach (var (axis, held) in _deadlines) {
                if (held.Deadline <= now) {
                    expired.Add(axis);
                }
            }
            expired.Sort();
            foreach (var axis in expired) {
                _deadlines.Remove(axis);
            }
            return expired;
        }
    }
}
