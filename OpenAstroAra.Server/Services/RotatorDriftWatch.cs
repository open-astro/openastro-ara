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

namespace OpenAstroAra.Server.Services;

/// <summary>
/// §42.2 (audit item 4) — detects a rotator whose reported angle drifts from the last commanded
/// target (mechanical slippage, a driver that quietly ignored the move, a belt that skipped after
/// settle). The angle-domain analog of <see cref="SwitchReadbackWatch"/>, sharing its
/// <see cref="ReadbackVerdict"/> episode protocol: only daemon-commanded moves are ever checked
/// (<see cref="Command"/> remembers the target + which angle domain it addressed), observations
/// while the device reports motion or inside the post-command settle window are ignored, then
/// <see cref="DefaultDriftThreshold"/> consecutive out-of-tolerance reads earn one
/// <see cref="ReadbackVerdict.Recommand"/> (the caller re-issues the SAME move once, re-arming the
/// settle window) before <see cref="ReadbackVerdict.Mismatch"/> fires — once per command episode.
/// An in-tolerance read after a fired episode drops the record: re-arming requires a fresh move,
/// so a rotator oscillating at the tolerance boundary can't churn out faults.
///
/// The comparison is a proper circular distance, so a commanded 359.8° reading back 0.1° is a
/// 0.3° drift, not 359.7°. Tolerance is absolute degrees (default
/// <see cref="DefaultToleranceDeg"/>) — a rotator's positioning error doesn't scale with its
/// range the way a switch port's does.
///
/// Not internally synchronized — the owning RotatorService mutates it only under its gate.
/// </summary>
public sealed class RotatorDriftWatch {

    /// <summary>Post-command wall-clock grace before angle reads count. Longer than the switch
    /// watch's: it also shields drivers without a working IsMoving (whose runtime reads "idle"
    /// while the mechanism still turns) from a false accusation on a slow move — combined with
    /// the streak that is ~16 s of physical motion before even the benign re-command.</summary>
    public static readonly TimeSpan DefaultSettleWindow = TimeSpan.FromSeconds(10);

    /// <summary>Consecutive out-of-tolerance reads before the verdict escalates
    /// (≈6 s at the 2 s refresh tick, after the settle window).</summary>
    public const int DefaultDriftThreshold = 3;

    /// <summary>Angular tolerance in degrees — drift at or under this is in-position.</summary>
    public const double DefaultToleranceDeg = 0.5;

    private sealed class MoveRecord {
        public float TargetDeg;
        public bool Mechanical;
        public DateTimeOffset CommandedUtc;
        public int DriftStreak;
        public bool Recommanded;
        public bool EpisodeFired;
    }

    private readonly TimeSpan _settleWindow;
    private readonly int _threshold;
    private MoveRecord? _move;

    public RotatorDriftWatch(TimeSpan? settleWindow = null, int threshold = DefaultDriftThreshold) {
        _settleWindow = settleWindow ?? DefaultSettleWindow;
        _threshold = threshold < 1 ? 1 : threshold;
    }

    /// <summary>A successfully dispatched move — remember the target and which angle domain
    /// (mechanical vs sky) it addressed; a fresh command is a fresh episode, even onto a fired
    /// one.</summary>
    public void Command(float targetDeg, bool mechanical, DateTimeOffset nowUtc) {
        _move = new MoveRecord { TargetDeg = targetDeg, Mechanical = mechanical, CommandedUtc = nowUtc };
    }

    /// <summary>Drop the expectation entirely — called when the angle frame is redefined (Sync,
    /// Reverse flip), when a move fails or is never confirmed settled (angle unknown), and on
    /// connect adoption / disconnect / connection-lost.</summary>
    public void Reset() => _move = null;

    /// <summary>The remembered move (null = none pending) — lets the caller re-issue the exact
    /// command on <see cref="ReadbackVerdict.Recommand"/> and report "commanded X, reads Y".</summary>
    public (float TargetDeg, bool Mechanical)? Commanded =>
        _move is null ? null : (_move.TargetDeg, _move.Mechanical);

    /// <summary>One call per refresh tick with that tick's runtime read. Reads while the device
    /// reports motion are <see cref="ReadbackVerdict.Settling"/> (a rotator en route is not
    /// drifting); a null angle in the commanded domain is skipped (an unreadable property must
    /// never accuse).</summary>
    public ReadbackVerdict Observe(double? mechanicalDeg, double? skyDeg, bool isMoving,
            double toleranceDeg, DateTimeOffset nowUtc) {
        if (_move is null) {
            return ReadbackVerdict.Idle;
        }
        if (isMoving || nowUtc - _move.CommandedUtc < _settleWindow) {
            return ReadbackVerdict.Settling;
        }
        var reported = _move.Mechanical ? mechanicalDeg : skyDeg;
        if (reported is not double angle) {
            return ReadbackVerdict.Idle; // can't read the commanded domain — no basis to accuse
        }
        if (AngularDistanceDeg(angle, _move.TargetDeg) <= Math.Abs(toleranceDeg)) {
            if (_move.EpisodeFired) {
                _move = null; // recovered — a fresh move re-arms
                return ReadbackVerdict.Cleared;
            }
            _move.DriftStreak = 0;
            return ReadbackVerdict.Idle;
        }
        if (_move.EpisodeFired) {
            return ReadbackVerdict.Idle; // still adrift, already reported
        }
        _move.DriftStreak++;
        if (_move.DriftStreak >= _threshold) {
            if (!_move.Recommanded) {
                // §42.2 — one re-issue of the same move before accusing; the streak and settle
                // window restart so the re-command gets the same fair observation.
                _move.Recommanded = true;
                _move.CommandedUtc = nowUtc;
                _move.DriftStreak = 0;
                return ReadbackVerdict.Recommand;
            }
            _move.EpisodeFired = true;
            return ReadbackVerdict.Mismatch;
        }
        return ReadbackVerdict.Degraded;
    }

    /// <summary>Shortest circular distance between two angles in degrees, in [0, 180].</summary>
    internal static double AngularDistanceDeg(double a, double b) {
        var d = Math.Abs(a - b) % 360.0;
        return d > 180.0 ? 360.0 - d : d;
    }
}
