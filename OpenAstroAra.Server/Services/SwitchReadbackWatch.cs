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

/// <summary>The watch's read on one written port after one refresh-tick observation.</summary>
public enum ReadbackVerdict {
    /// <summary>Nothing to report — port never written, in tolerance, or already reported.</summary>
    Idle,

    /// <summary>Inside the post-write settle window — a slow relay/PWM ramp is not a mismatch.</summary>
    Settling,

    /// <summary>Out of tolerance, but not for enough consecutive reads to accuse — one flapped
    /// read (a transient driver hiccup) must not fire a fault (§42.4).</summary>
    Degraded,

    /// <summary>The read-back persistently disagrees — the §42.2 "Re-command" recovery step
    /// (rows 15/16): the caller re-issues the SAME commanded value once, and the record re-arms
    /// its settle window. Fired at most once per command episode, before <see cref="Mismatch"/>.</summary>
    Recommand,

    /// <summary>The read-back persistently disagrees with the commanded value even after the
    /// one re-command: the caller publishes
    /// <see cref="Contracts.EquipmentFaultKind.ValueMismatch"/> exactly once per command episode.</summary>
    Mismatch,

    /// <summary>Back in tolerance after a fired episode — the record is dropped; re-arming
    /// requires a fresh write.</summary>
    Cleared,
}

/// <summary>
/// §42.4 — detects a written ISwitch port whose read-back doesn't match what was commanded
/// (a stuck relay, a driver that quietly ignored the write, a dew-heater controller that
/// browned out). Only ports the daemon actually wrote are ever checked: <see cref="Command"/>
/// remembers the commanded value + timestamp, reads inside the wall-clock settle window are
/// ignored (writes are event-driven, not tick-aligned, so the window can't be tick-counted),
/// then <see cref="DefaultMismatchThreshold"/> consecutive out-of-tolerance reads fire once
/// per command episode. Tolerance is a percentage of the port's min/max range — a boolean
/// port's range is 1, so 5% catches a commanded-ON/read-OFF flip. An in-tolerance read after
/// a fired episode drops the record entirely: re-arming requires a fresh write, so a port
/// oscillating at the tolerance boundary can't churn out faults.
///
/// Not internally synchronized — the owning SwitchService keeps one instance per
/// SwitchConnection and mutates it only under its connection gate.
/// </summary>
public sealed class SwitchReadbackWatch {

    /// <summary>Post-write settle grace before read-backs count.</summary>
    public static readonly TimeSpan DefaultSettleWindow = TimeSpan.FromSeconds(5);

    /// <summary>Consecutive out-of-tolerance reads before the mismatch is declared
    /// (≈6 s at the 2 s refresh tick, after the settle window).</summary>
    public const int DefaultMismatchThreshold = 3;

    private sealed class PortRecord {
        public double Commanded;
        public DateTimeOffset WrittenUtc;
        public int MismatchStreak;
        public bool Recommanded;
        public bool EpisodeFired;
    }

    private readonly TimeSpan _settleWindow;
    private readonly int _threshold;
    private readonly Dictionary<short, PortRecord> _written = [];

    public SwitchReadbackWatch(TimeSpan? settleWindow = null, int threshold = DefaultMismatchThreshold) {
        _settleWindow = settleWindow ?? DefaultSettleWindow;
        _threshold = threshold < 1 ? 1 : threshold;
    }

    /// <summary>A successful write to a port — remember the commanded value and restart that
    /// port's episode (a fresh command is a fresh expectation, even onto a fired episode).</summary>
    public void Command(short portId, double value, DateTimeOffset nowUtc) {
        _written[portId] = new PortRecord { Commanded = value, WrittenUtc = nowUtc };
    }

    /// <summary>Fresh session — called at connect adoption, disconnect, and connection-lost trip.</summary>
    public void Reset() => _written.Clear();

    /// <summary>The remembered commanded value for a port (null = never written / cleared) —
    /// lets the fault publisher report "commanded X, reads Y" without re-deriving it.</summary>
    public double? CommandedFor(short portId) =>
        _written.TryGetValue(portId, out var record) ? record.Commanded : null;

    /// <summary>One call per refresh tick per snapshot port. Ports without a remembered write
    /// return <see cref="ReadbackVerdict.Idle"/> immediately.</summary>
    public ReadbackVerdict Observe(short portId, double readBack, double min, double max,
            double tolerancePct, DateTimeOffset nowUtc) {
        if (!_written.TryGetValue(portId, out var record)) {
            return ReadbackVerdict.Idle;
        }
        if (nowUtc - record.WrittenUtc < _settleWindow) {
            return ReadbackVerdict.Settling;
        }
        // Tolerance as a fraction of the port's range; a degenerate (≤0) range — some drivers
        // report min==max for boolean ports — falls back to near-exact matching.
        var range = max - min;
        var allowed = range > 0 ? Math.Abs(tolerancePct) / 100.0 * range : 1e-9;
        if (Math.Abs(readBack - record.Commanded) <= allowed) {
            if (record.EpisodeFired) {
                _written.Remove(portId); // recovered — a fresh write re-arms
                return ReadbackVerdict.Cleared;
            }
            record.MismatchStreak = 0;
            return ReadbackVerdict.Idle;
        }
        if (record.EpisodeFired) {
            return ReadbackVerdict.Idle; // still wrong, already reported
        }
        record.MismatchStreak++;
        if (record.MismatchStreak >= _threshold) {
            if (!record.Recommanded) {
                // §42.2 rows 15/16 — "Re-command → Notify if still off": give the port one
                // re-issue of the same value before accusing. The streak and settle window
                // restart so the re-command gets the same fair observation as the original
                // write; a second exhaustion fires the fault.
                record.Recommanded = true;
                record.WrittenUtc = nowUtc;
                record.MismatchStreak = 0;
                return ReadbackVerdict.Recommand;
            }
            record.EpisodeFired = true;
            return ReadbackVerdict.Mismatch;
        }
        return ReadbackVerdict.Degraded;
    }
}
