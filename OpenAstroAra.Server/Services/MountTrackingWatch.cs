#region "copyright"

/*
    Copyright (c) 2026 Open Astro and the OpenAstro Ara contributors

    This file is part of OpenAstro Ara (forked from N.I.N.A.).

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

namespace OpenAstroAra.Server.Services;

/// <summary>The watch's read on the mount after one refresh-tick observation.</summary>
public enum TrackingWatchVerdict {
    /// <summary>Nothing to report — not armed, mount in motion/parked, or an episode already reported.</summary>
    Idle,

    /// <summary>Inside the post-command grace window — observations sync expectations, never accuse.</summary>
    Suppressed,

    /// <summary>Tracking reads off while expected on, but not for long enough to call it lost —
    /// a single flapped read (the per-field read maps a transient failure to <c>false</c>) must
    /// not fire a fault (§42.2).</summary>
    Degraded,

    /// <summary>Tracking silently dropped: off for the threshold streak with no daemon command
    /// that explains it. The caller publishes <see cref="Contracts.EquipmentFaultKind.TrackingLost"/>
    /// exactly once per episode.</summary>
    Lost,

    /// <summary>Tracking is back on after a fired episode (the §42.3 re-enable succeeded, or the
    /// user fixed it) — the watch re-arms for a fresh episode.</summary>
    Recovered,
}

/// <summary>
/// §42.2 — detects a mount silently dropping tracking (the "Alpaca <c>Tracking = false</c>
/// unexpectedly" row): tracking was observed on, reads off for
/// <see cref="DefaultDropThreshold"/> consecutive refresh ticks (≈6 s at the 2 s tick — the
/// per-field read maps transient failures to <c>false</c>, so single-tick firing is unsafe),
/// and no daemon-issued command explains it. Commands that legitimately change tracking
/// (SetTracking, Park, slews, axis moves) are noted BEFORE dispatch and arm a
/// <see cref="DefaultCommandGraceTicks"/>-tick grace window during which observations update
/// expectations instead of accusing; an observed slew or park is likewise never a drop.
/// Fires once per episode; only an observed tracking=true (or a deliberate tracking-off /
/// park command) re-arms — a §42.3 re-enable the mount rejects must NOT re-fire and loop
/// the reaction service.
///
/// Not internally synchronized — the owning TelescopeService mutates it only under its
/// connection gate (the same discipline as <see cref="DeviceConnectionProbe"/>).
/// </summary>
public sealed class MountTrackingWatch {

    /// <summary>Consecutive tracking-off observations before the drop is declared.</summary>
    public const int DefaultDropThreshold = 3;

    /// <summary>Refresh ticks of grace after any mount command (≈10 s at the 2 s tick) —
    /// covers the window where the command's effect hasn't reached the polled state yet
    /// (e.g. a slew commanded but <c>Slewing</c> not asserted).</summary>
    public const int DefaultCommandGraceTicks = 5;

    private readonly int _threshold;
    private readonly int _graceTicks;
    private bool _expectTracking;
    private int _dropStreak;
    private int _graceRemaining;
    private bool _episodeFired;

    public MountTrackingWatch(int threshold = DefaultDropThreshold, int graceTicks = DefaultCommandGraceTicks) {
        _threshold = threshold < 1 ? 1 : threshold;
        _graceTicks = graceTicks < 0 ? 0 : graceTicks;
    }

    /// <summary>A deliberate SetTracking command. Tracking-off clears any fired episode
    /// (the user/daemon chose off — the next on starts fresh); tracking-on does NOT — a
    /// rejected §42.3 re-enable must not re-fire, only an observed recovery re-arms.</summary>
    public void NoteTrackingCommanded(bool enabled) {
        _expectTracking = enabled;
        _dropStreak = 0;
        _graceRemaining = _graceTicks;
        if (!enabled) {
            _episodeFired = false;
        }
    }

    /// <summary>A park/home command — parking legitimately ends with tracking off.</summary>
    public void NoteParkCommanded() {
        _expectTracking = false;
        _dropStreak = 0;
        _graceRemaining = _graceTicks;
        _episodeFired = false;
    }

    /// <summary>Any other motion command (slew, unpark, axis move, abort) — arms the grace
    /// window without changing what tracking state is expected afterwards.</summary>
    public void NoteMotionCommanded() {
        _dropStreak = 0;
        _graceRemaining = _graceTicks;
    }

    /// <summary>Fresh session — called at connect adoption and on connection-lost trip.</summary>
    public void Reset() {
        _expectTracking = false;
        _dropStreak = 0;
        _graceRemaining = 0;
        _episodeFired = false;
    }

    /// <summary>One call per refresh tick with the freshly-read runtime state.</summary>
    public TrackingWatchVerdict Observe(bool tracking, bool slewing, bool parked) {
        if (_graceRemaining > 0) {
            _graceRemaining--;
            _dropStreak = 0;
            // Sync expectations from whatever the command settled into.
            if (parked) {
                _expectTracking = false;
            } else if (tracking && !slewing) {
                _expectTracking = true;
            }
            return TrackingWatchVerdict.Suppressed;
        }
        if (slewing || parked) {
            _dropStreak = 0;
            if (parked) {
                _expectTracking = false;
            }
            return TrackingWatchVerdict.Idle;
        }
        if (tracking) {
            // Arms the watch even for handset/driver-initiated tracking the daemon never commanded.
            _expectTracking = true;
            _dropStreak = 0;
            if (_episodeFired) {
                _episodeFired = false;
                return TrackingWatchVerdict.Recovered;
            }
            return TrackingWatchVerdict.Idle;
        }
        if (!_expectTracking || _episodeFired) {
            return TrackingWatchVerdict.Idle;
        }
        _dropStreak++;
        if (_dropStreak >= _threshold) {
            _episodeFired = true;
            _dropStreak = 0;
            return TrackingWatchVerdict.Lost;
        }
        return TrackingWatchVerdict.Degraded;
    }
}
