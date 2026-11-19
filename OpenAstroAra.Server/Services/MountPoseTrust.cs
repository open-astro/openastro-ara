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
/// means the mount's model reset);</item>
/// <item>inside a session, AtHome turns true (<see cref="AtHomeWithoutCommand"/>) or Dec jumps
/// (<see cref="PoseJumpInSession"/>) between two still ticks with no Ara mount command and no
/// slewing tick within <see cref="QuietWindow"/> (a power blip the session survived).</item>
/// </list>
/// Only <see cref="Confirm"/> (the user says the mount is at Home) clears it; a new session for the
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

    /// <summary>Not a stored reason: the session's first position read has not been observed yet.</summary>
    public const string NotReadYet = "not_read_yet";

    /// <summary>A Dec change larger than this between two still readings, with nothing commanded, is a jump.</summary>
    public const double PoseJumpDeg = 2.0;

    /// <summary>How long after an Ara mount command or a slewing tick an in-session change is explained by it.</summary>
    public static readonly TimeSpan QuietWindow = TimeSpan.FromSeconds(30);

    private string? _deviceId;
    private bool _observed;
    private string? _reason;
    // The last still pose read while the position was trusted, for the same device (survives sessions).
    private (bool AtHome, double DecDeg)? _lastTrusted;
    // The previous observed tick of this session, for the in-session checks.
    private (bool AtHome, double DecDeg)? _previous;
    private DateTimeOffset _lastMotionAt = DateTimeOffset.MinValue;
    // Axes Ara set moving with MoveAxis and has not stopped (bit per axis): a driver that does not report
    // Slewing during MoveAxis must not make a long pad press look like a power blip.
    private int _movingAxes;

    /// <summary>The device the state belongs to (null before the first session).</summary>
    public string? DeviceId => _deviceId;

    /// <summary>True once this session's first position read was observed.</summary>
    public bool Observed => _observed;

    /// <summary>Null while the position is trusted; otherwise why it is not (one of the reason constants).</summary>
    public string? Reason => _reason;

    /// <summary>Null when motion may go ahead; otherwise the reason it may not (<see cref="NotReadYet"/>
    /// before this session's first read).</summary>
    public string? BlockReason => _reason ?? (_observed ? null : NotReadYet);

    /// <summary>A new session (connect, reconnect, trip or disconnect). The flag and the last trusted
    /// pose carry over for the same device; another device starts from <paramref name="restoredReason"/>
    /// (what the owner persisted for it) and no history.</summary>
    public void BeginSession(string? deviceId, string? restoredReason) {
        if (!string.Equals(deviceId, _deviceId, StringComparison.Ordinal)) {
            _deviceId = deviceId;
            _reason = restoredReason;
            _lastTrusted = null;
        }
        _observed = false;
        _previous = null;
        _lastMotionAt = DateTimeOffset.MinValue;
        _movingAxes = 0;
    }

    /// <summary>An Ara mount command (goto, sync, park, home, axis move, abort, tracking) is about to be sent.</summary>
    public void NoteCommand(DateTimeOffset now) => _lastMotionAt = now;

    /// <summary>An Ara MoveAxis started (or renewed) <paramref name="moving"/> = true, or stopped one axis.</summary>
    public void NoteAxisMove(int axis, bool moving, DateTimeOffset now) {
        _lastMotionAt = now;
        if (axis is < 0 or > 30) {
            return;
        }
        _movingAxes = moving ? _movingAxes | (1 << axis) : _movingAxes & ~(1 << axis);
    }

    /// <summary>An abort stopped every axis.</summary>
    public void NoteAllAxesStopped(DateTimeOffset now) {
        _lastMotionAt = now;
        _movingAxes = 0;
    }

    /// <summary>One position reading (a refresh tick, or the guards' on-demand read before the first tick).
    /// <paramref name="decDeg"/> null means the position read failed: nothing is decided. True when this
    /// reading made the position unverified (the owner logs and persists it).</summary>
    public bool Observe(bool atHome, bool slewing, double? decDeg, DateTimeOffset now) {
        if (decDeg is not double dec || !double.IsFinite(dec)) {
            return false;
        }
        if (slewing || _movingAxes != 0) {
            _lastMotionAt = now;
        }
        var flagged = false;
        if (!_observed) {
            _observed = true;
            if (_reason is null && !slewing) {
                if (atHome && _lastTrusted is not { AtHome: true }) {
                    _reason = AtHomeOnConnect;
                } else if (_lastTrusted is { } trusted && Math.Abs(dec - trusted.DecDeg) > PoseJumpDeg) {
                    _reason = PoseJumpOnReconnect;
                }
                flagged = _reason is not null;
            }
        } else if (_reason is null && !slewing && _previous is { } prev && now - _lastMotionAt > QuietWindow) {
            if (atHome && !prev.AtHome) {
                _reason = AtHomeWithoutCommand;
            } else if (Math.Abs(dec - prev.DecDeg) > PoseJumpDeg) {
                _reason = PoseJumpInSession;
            }
            flagged = _reason is not null;
        }
        _previous = slewing ? null : (atHome, dec);
        if (_reason is null && !slewing) {
            _lastTrusted = (atHome, dec);
        }
        return flagged;
    }

    /// <summary>The user confirmed the mount is physically at Home (the owner checked that it reports
    /// AtHome). Clears the flag; the confirmed Home becomes the last trusted pose. True when it cleared one.</summary>
    public bool Confirm(double? decDeg) {
        var cleared = _reason is not null;
        _reason = null;
        _observed = true;
        if (decDeg is double dec && double.IsFinite(dec)) {
            _lastTrusted = (true, dec);
            _previous = (true, dec);
        }
        return cleared;
    }
}
