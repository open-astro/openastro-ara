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
/// #2070 — whether Ara may trust the mount's reported position. A mount with no absolute encoders
/// (the iOptron HAE29C) wakes from a power loss reporting "at zero" (AtHome, Dec 90) wherever it
/// physically points; a GoTo, Park, model Home or polar alignment from that false zero drives the
/// tube to the wrong place (RC51, 2026-11-19). The position is <b>unverified</b> when:
/// <list type="bullet">
/// <item>the first read of a session reports AtHome while still (<see cref="AtHomeOnConnect"/>),
/// unless the last pose this process trusted for the same device was itself a trusted Home (a link
/// blip while at a real Home stays trusted);</item>
/// <item>the first read of a session is more than <see cref="PoseJumpDeg"/> in Dec from the last
/// trusted pose (<see cref="PoseJumpOnReconnect"/>; Dec does not move with tracking, so a jump
/// means the mount's model reset). Only a pose the mount had settled at counts: a session that ended
/// mid-move (a link trip during a GoTo) leaves none, so the finished slew is not read as a jump;</item>
/// <item>inside a session, AtHome turns true (<see cref="AtHomeWithoutCommand"/>) or Dec jumps
/// (<see cref="PoseJumpInSession"/>) between two still ticks with no Ara mount command and no
/// slewing tick within <see cref="QuietWindow"/> (a power blip the session survived).</item>
/// </list>
/// #2173 — a verdict is never taken from one sample: a jump (or Home) must show on two consecutive readings
/// (<see cref="PendingVerdict"/>), so one bad read (AlpacaBridge's placeholder after a failed status poll, the first
/// read after a link fault or a reconnect) never flags. A Dec past ±90° is folded back over the pole first
/// (<see cref="FoldDec"/>), as Polar Align's fresh reads fold it. An Ara GoTo, park or Home whose call or arrival wait
/// is still running (<see cref="BeginCommandedMove"/>) explains every change, and a Dec that lands at the last
/// commanded GoTo's target (<see cref="NoteGotoTarget"/>) is no jump, however long the slew took.
/// While a verdict waits for its second reading, motion is refused (<see cref="BlockReason"/>), and a command or move in
/// that window does not wash it out: the next still reading settles it (see <see cref="Observe"/>).
/// A routine that drove the motors flags it too (<see cref="StopUnconfirmed"/>): its stop was not confirmed, or the mount
/// reconnected while it ran (#2066). A reading that suggests one of the model-suspect reasons replaces it (#2173 review, see
/// <see cref="HomeSearchMayTrust"/>).
/// Only <see cref="Confirm"/> (the user says the mount is at Home, or #2173 an Ara Home seen to run ended at Home while the reason, or the mount's sensor-search capability, allows it, <see cref="HomeSearchMayTrust"/>) clears it; a new session for the
/// same device keeps it, and the owner persists it per device so a daemon restart keeps it too.
/// Until a session's first read is observed the mount is not trusted either (<see cref="Observed"/>).
///
/// Not internally synchronized: the owning <see cref="TelescopeService"/> calls it under its gate,
/// like <see cref="MountTrackingWatch"/> and <see cref="TargetDisplayLatch"/>. It does no IO.
/// </summary>
internal sealed class MountPoseTrust {

    public const string AtHomeOnConnect = "at_home_on_connect";
    public const string PoseJumpOnReconnect = "pose_jump_on_reconnect";
    public const string AtHomeWithoutCommand = "at_home_without_command";
    public const string PoseJumpInSession = "pose_jump_in_session";

    /// <summary>#2066, #2065 (ARA-10) — a routine that drove the motors (Polar Align) could not confirm the mount stopped
    /// (<c>mount_did_not_stop</c>), or the mount's session changed (a reconnect, a power cycle) while the routine ran: the
    /// mount may have moved on, so its reported position is not trusted. Set by the routine (<see cref="Flag"/>).</summary>
    public const string StopUnconfirmed = "stop_unconfirmed";

    /// <summary>Not a stored reason: the session's first position read has not been observed yet.</summary>
    public const string NotReadYet = "not_read_yet";

    /// <summary>#2173 review — whether an Ara Home seen to run that ends at Home may trust the position by itself, with no
    /// eye check. Ara cannot tell a real home-sensor search from a Home that drives to where the mount's own position model
    /// says Home is (an iOptron <c>:MH#</c>), and ADR 0003 rule 2 forbids asking the brand. So a completed Home trusts the
    /// position only when the reason leaves the mount's own model intact and only Ara's knowledge is in doubt. When the
    /// model itself may be wrong, a model Home drives back to the model's false zero (a pad nudge first, then Home, is
    /// seen to run and lands at Home), so Home stays allowed as the way out but the user confirms Home by eye after it
    /// (<c>confirm-home</c>). Each reason:
    /// <list type="bullet">
    /// <item><see cref="StopUnconfirmed"/>: may trust. The mount kept power and its model; only Ara did not see it stop. A
    /// power cycle while the routine ran also wakes the mount reporting Home, and that reading replaces this reason with a
    /// model-suspect one (<see cref="Observe"/>).</item>
    /// <item><see cref="AtHomeOnConnect"/>: eye check. A mount with no absolute encoders that lost power wakes at a false
    /// zero: its model is what is wrong.</item>
    /// <item><see cref="PoseJumpOnReconnect"/>: eye check. The model moved while Ara was away, as after a power loss or a
    /// reset.</item>
    /// <item><see cref="AtHomeWithoutCommand"/>: eye check. The model jumped to Home in session, as after a power blip.</item>
    /// <item><see cref="PoseJumpInSession"/>: eye check. The model jumped with nothing commanded: a blip, a reset or a
    /// slipped clutch.</item>
    /// <item>Any other stored reason (an older daemon's, or an unknown one): eye check, fail safe.</item>
    /// </list>
    /// Null (already trusted) may trust: the Home then only renews the trusted pose.
    /// <para>#2173 — <paramref name="homeIsSensorSearch"/>: the driver itself says its FindHome is a real home-sensor
    /// search (AlpacaBridge's <c>findhomeissensorsearch</c> capability, read once per connect). A sensor search finds the
    /// physical Home whatever the model says, so every reason may trust, the model-suspect ones included. Only that
    /// capability decides, never a brand, model or driver name (ADR 0003 rule 2). The watch's guards (seen to run, two
    /// still reads at Home, a gap or a reconnect wipes the evidence) apply either way.</para></summary>
    public static bool HomeSearchMayTrust(string? reason, bool homeIsSensorSearch = false) =>
        homeIsSensorSearch || reason is null or StopUnconfirmed;

    // #2173 review — a verdict the mount's readings make may still land while only Ara's knowledge is in doubt
    // (StopUnconfirmed): a model-suspect reason replaces it, so a power cycle after an unconfirmed stop needs the eye check.
    private bool VerdictsOpen => _reason is null or StopUnconfirmed;

    /// <summary>A Dec change larger than this between two still readings, with nothing commanded, is a jump.</summary>
    public const double PoseJumpDeg = 2.0;

    /// <summary>How long after an Ara mount command or a slewing tick an in-session change is explained by it.</summary>
    public static readonly TimeSpan QuietWindow = TimeSpan.FromSeconds(30);

    /// <summary>#2173 — how much later than the reading that suggested a verdict the agreeing one must be: a driver that
    /// serves a status from a short cache (AlpacaBridge's iOptron keeps it 1 s) answers one bad poll twice.</summary>
    public static readonly TimeSpan ConfirmGap = TimeSpan.FromSeconds(2);

    private string? _deviceId;
    private bool _observed;
    private string? _reason;
    // The last still pose read while the position was trusted, for the same device (survives sessions).
    private (bool AtHome, double DecDeg)? _lastTrusted;
    // True when the mount may have moved since _lastTrusted was read (an Ara command, a slewing tick or an
    // axis move came after it, or within QuietWindow before it). A session that ends so (a link trip in the
    // middle of a GoTo) leaves no settled pose: the next session must not read the finished slew as a jump.
    private bool _motionSinceTrusted;
    // The previous observed tick of this session, for the in-session checks.
    private (bool AtHome, double DecDeg)? _previous;
    private DateTimeOffset _lastMotionAt = DateTimeOffset.MinValue;
    // Axes Ara set moving with MoveAxis and has not stopped (bit per axis): a driver that does not report
    // Slewing during MoveAxis must not make a long pad press look like a power blip.
    private int _movingAxes;
    // #2173 — Ara moves in flight (a GoTo, park or Home whose call or arrival wait runs), counted per session.
    private int _commandedMoves;
    private int _sessionGeneration;
    // #2173 — the folded Dec of the last Ara GoTo target, until a still reading reaches it.
    private double? _commandedDec;
    // #2173 — a verdict one reading made, waiting for the next to agree (null: none).
    private (string Reason, bool AtHome, double DecDeg, DateTimeOffset At)? _pending;

    /// <summary>The device the state belongs to (null before the first session).</summary>
    public string? DeviceId => _deviceId;

    /// <summary>True once this session's first position read was observed.</summary>
    public bool Observed => _observed;

    /// <summary>Null while the position is trusted; otherwise why it is not (one of the reason constants).</summary>
    public string? Reason => _reason;

    /// <summary>Null when motion may go ahead; otherwise the reason it may not (<see cref="NotReadYet"/>
    /// before this session's first read, and #2173 while a reading's verdict waits for the next to agree).</summary>
    public string? BlockReason => _reason ?? (_observed && _pending is null ? null : NotReadYet);

    /// <summary>#2173 — the reason one reading suggested and the next has not yet agreed with (null: none). Not a flag,
    /// but motion is refused while it waits (<see cref="BlockReason"/> is <see cref="NotReadYet"/>): a GoTo sent in that
    /// window would drive by a model that may just have reset.</summary>
    public string? PendingVerdict => _pending?.Reason;

    /// <summary>#2173 — a Dec past ±90° (a Dec axis over the pole, as an iOptron at Home reads +90.71°) folded back to the
    /// same pointing on the near side (180° − Dec), as Polar Align's fresh reads fold it. Null when it is no Dec at all
    /// (not finite, or past ±180°).</summary>
    public static double? FoldDec(double decDeg) {
        if (!double.IsFinite(decDeg) || Math.Abs(decDeg) > 180.0) {
            return null;
        }
        return Math.Abs(decDeg) > 90.0 ? Math.CopySign(180.0, decDeg) - decDeg : decDeg;
    }

    /// <summary>A new session (connect, reconnect, trip or disconnect). The flag and the last trusted
    /// pose carry over for the same device; another device starts from <paramref name="restoredReason"/>
    /// (what the owner persisted for it) and no history.</summary>
    public void BeginSession(string? deviceId, string? restoredReason) {
        if (!string.Equals(deviceId, _deviceId, StringComparison.Ordinal)) {
            _deviceId = deviceId;
            _reason = restoredReason;
            _lastTrusted = null;
        } else if (_motionSinceTrusted) {
            // The last session ended with the mount (possibly) moving: its last pose says nothing about where
            // the mount is now. With no history, a Home read on connect is unverified again (fail-safe).
            _lastTrusted = null;
        }
        _motionSinceTrusted = false;
        _observed = false;
        _previous = null;
        _lastMotionAt = DateTimeOffset.MinValue;
        _movingAxes = 0;
        _commandedMoves = 0;
        _sessionGeneration++;
        _commandedDec = null;
        _pending = null;
    }

    /// <summary>#2173 — an Ara GoTo, park or Home is sent and its call (or its arrival wait) runs: every change until
    /// <see cref="EndCommandedMove"/> is explained, and the quiet window counts from its end. Returns the session's
    /// generation, which the end hands back: an end from an older session changes nothing. A GoTo passes its
    /// <paramref name="gotoTargetDecDeg"/> (<see cref="NoteGotoTarget"/>).</summary>
    public int BeginCommandedMove(DateTimeOffset now, double? gotoTargetDecDeg = null) {
        if (gotoTargetDecDeg is double target) {
            NoteGotoTarget(target, now);
        }
        NoteCommand(now);
        _commandedMoves++;
        return _sessionGeneration;
    }

    /// <summary>#2173 — the move <see cref="BeginCommandedMove"/> began is over: it <paramref name="arrived"/>, or it
    /// failed or was abandoned, when a GoTo's target no longer explains a later Dec (<see cref="ClearGotoTarget"/>).</summary>
    public void EndCommandedMove(int generation, DateTimeOffset now, bool arrived) {
        if (generation != _sessionGeneration || _commandedMoves == 0) {
            return;
        }
        _commandedMoves--;
        NoteCommand(now);
        if (!arrived) {
            ClearGotoTarget();
        }
    }

    /// <summary>#2173 — the last GoTo was not sent, was refused or failed: a later Dec near its target is no longer its
    /// own (a jump there would otherwise be excused for the rest of the session).</summary>
    public void ClearGotoTarget() => _commandedDec = null;

    /// <summary>#2173 — an Ara GoTo to <paramref name="targetDecDeg"/> is about to be sent: a Dec that lands there is the
    /// GoTo's, never a jump, even when the slew outlasted the quiet window with no slewing tick read.</summary>
    public void NoteGotoTarget(double targetDecDeg, DateTimeOffset now) {
        NoteCommand(now);
        _commandedDec = FoldDec(targetDecDeg);
    }

    /// <summary>An Ara mount command (goto, sync, park, home, axis move, abort, tracking) is about to be sent.</summary>
    public void NoteCommand(DateTimeOffset now) {
        _lastMotionAt = now;
        _motionSinceTrusted = true;
    }

    /// <summary>An Ara MoveAxis started (or renewed) <paramref name="moving"/> = true, or stopped one axis.</summary>
    public void NoteAxisMove(int axis, bool moving, DateTimeOffset now) {
        _lastMotionAt = now;
        _motionSinceTrusted = true;
        if (axis is < 0 or > 30) {
            return;
        }
        _movingAxes = moving ? _movingAxes | (1 << axis) : _movingAxes & ~(1 << axis);
    }

    /// <summary>An abort stopped every axis.</summary>
    public void NoteAllAxesStopped(DateTimeOffset now) {
        _lastMotionAt = now;
        _motionSinceTrusted = true;
        _movingAxes = 0;
        _commandedDec = null; // the GoTo, if any, was stopped short of its target
    }

    /// <summary>One position reading (a refresh tick, or the guards' on-demand read before the first tick).
    /// <paramref name="decDeg"/> null means the position read failed: nothing is decided. True when this
    /// reading made the position unverified (the owner logs and persists it). #2173 — a reading that suggests a
    /// verdict only flags when the next reading agrees with it; a Dec past the pole is folded first.</summary>
    public bool Observe(bool atHome, bool slewing, double? decDeg, DateTimeOffset now) {
        if (decDeg is not double raw || FoldDec(raw) is not double dec) {
            return false;
        }
        var moving = slewing || _movingAxes != 0 || _commandedMoves != 0;
        if (moving) {
            _lastMotionAt = now;
            _motionSinceTrusted = true;
        }
        // #2173 review — a verdict waiting for its second reading when a command or a move came: the move must not wash
        // it out (a moving or quiet-window reading decides nothing, so the jumped pose would become the trusted one).
        // A reading that agrees with it confirms it; the mount back where it was before calls it an outlier; the mount
        // still somewhere else after the move flags it (fail safe: Ara cannot tell the jump from the move).
        if (VerdictsOpen && _pending is { } waiting && _lastMotionAt >= waiting.At) {
            if (Agrees(waiting, atHome, dec)) {
                return now - waiting.At >= ConfirmGap && Settle(waiting.Reason, atHome, dec);
            }
            if (moving) {
                return false; // wait for the move to end: a reading mid-move says nothing about the jump
            }
            if (_previous is not { } before || before.AtHome != atHome || Math.Abs(dec - before.DecDeg) > PoseJumpDeg) {
                return Settle(waiting.Reason, atHome, dec);
            }
            _pending = null; // back at the pose before the odd reading: that one was an outlier, and this one goes on below
        }
        string? verdict = null;
        if (!_observed) {
            if (VerdictsOpen && !slewing) {
                if (atHome && _lastTrusted is not { AtHome: true }) {
                    verdict = AtHomeOnConnect;
                } else if (_lastTrusted is { } trusted && Math.Abs(dec - trusted.DecDeg) > PoseJumpDeg) {
                    verdict = PoseJumpOnReconnect;
                }
            }
        } else if (VerdictsOpen && !moving && _previous is { } prev && now - _lastMotionAt > QuietWindow) {
            if (atHome && !prev.AtHome) {
                verdict = AtHomeWithoutCommand;
            } else if (Math.Abs(dec - prev.DecDeg) > PoseJumpDeg && !AtCommandedTarget(dec)) {
                verdict = PoseJumpInSession;
            }
        }
        if (verdict is not null) {
            if (_pending is not { } pending || pending.Reason != verdict || !Agrees(pending, atHome, dec)) {
                // One reading: wait for the next. The pose it reported is not taken as the previous or the trusted
                // one, so an outlier leaves nothing behind.
                _pending = (verdict, atHome, dec, now);
                return false;
            }
            if (now - pending.At < ConfirmGap) {
                return false; // too soon to be a fresh reading of its own: wait for a later one
            }
            return Settle(verdict, atHome, dec);
        }
        _pending = null;
        _observed = true;
        _previous = slewing ? null : (atHome, dec);
        if (_reason is null && !slewing) {
            _lastTrusted = (atHome, dec);
            _motionSinceTrusted = _movingAxes != 0 || _commandedMoves != 0 || now - _lastMotionAt <= QuietWindow;
            if (AtCommandedTarget(dec)) {
                _commandedDec = null; // arrived: a later jump to the same Dec is no longer the GoTo's
            }
        }
        return false;
    }

    private static bool Agrees((string Reason, bool AtHome, double DecDeg, DateTimeOffset At) pending, bool atHome, double dec) =>
        pending.AtHome == atHome && Math.Abs(dec - pending.DecDeg) <= PoseJumpDeg;

    // The verdict stands: the position is unverified from this reading on.
    private bool Settle(string reason, bool atHome, double dec) {
        _pending = null;
        _observed = true;
        _reason = reason;
        _previous = (atHome, dec);
        return true;
    }

    private bool AtCommandedTarget(double dec) => _commandedDec is double target && Math.Abs(dec - target) <= PoseJumpDeg;

    /// <summary>#2066 — a routine marks the position unverified (<see cref="StopUnconfirmed"/>). An earlier reason stays: it
    /// says first why the mount is not trusted, and only <see cref="Confirm"/> clears either. True when this set it.</summary>
    public bool Flag(string reason) {
        if (_reason is not null) {
            return false;
        }
        _reason = reason;
        return true;
    }

    /// <summary>The user confirmed the mount is physically at Home (the owner checked that it reports
    /// AtHome). Clears the flag; the confirmed Home becomes the last trusted pose. True when it cleared one.</summary>
    public bool Confirm(double? decDeg) {
        var cleared = _reason is not null;
        _reason = null;
        _observed = true;
        _pending = null;
        if (decDeg is double raw && FoldDec(raw) is double dec) {
            _lastTrusted = (true, dec);
            _previous = (true, dec);
            _motionSinceTrusted = false; // the owner read the mount at Home and not slewing
        }
        return cleared;
    }
}
