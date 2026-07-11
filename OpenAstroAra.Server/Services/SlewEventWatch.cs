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
/// §57.8 — turns the telescope status poll into slew lifecycle events, exactly like
/// <see cref="MountTrackingWatch"/> turns it into tracking-lost detection: observed transitions,
/// not instrumented call sites, so EVERY slew source (REST, sequencer mediator, meridian flip,
/// recovery, park/home) gets the events for free. Observed under the service's commit lock.
/// The 2 s poll cadence doubles as the §57.2 grace debounce — a sub-slew gap shorter than a
/// tick is never observed, so chained slews read as one episode.
/// </summary>
internal sealed class SlewEventWatch {

    internal enum Kind { None, Started, Completed }

    internal readonly record struct Verdict(
        Kind Kind,
        double? TargetRaHours = null,
        double? TargetDecDegrees = null,
        double DurationSeconds = 0);

    private bool _slewing;
    private long _startedAtTickMs;
    // Noted at slew-command time so the Started event can carry the intent; a park/home slew has
    // no coordinate target and publishes nulls.
    private double? _pendingTargetRa;
    private double? _pendingTargetDec;
    // A §57.4 abort suppresses the episode's Completed verdict — the abort path publishes its own
    // telescope.slew_aborted at abort time (immediately, not a poll-tick later).
    private bool _abortNoted;

    // Monotonic; injectable for tests (the TimeSyncService pattern).
    internal Func<long> TickMs = () => Environment.TickCount64;

    public void NoteSlewTarget(double raHours, double decDegrees) {
        _pendingTargetRa = raHours;
        _pendingTargetDec = decDegrees;
        // A fresh commanded slew supersedes any stale abort verdict from a previous episode.
        _abortNoted = false;
    }

    public void NoteAborted() =>
        // Unconditional: an abort landing between ticks (slew never observed as started) must
        // still suppress the Started/Completed pair should the mount briefly read as slewing.
        _abortNoted = true;

    public Verdict Observe(bool slewing) {
        if (slewing && !_slewing) {
            _slewing = true;
            _startedAtTickMs = TickMs();
            var verdict = new Verdict(Kind.Started, _pendingTargetRa, _pendingTargetDec);
            _pendingTargetRa = null;
            _pendingTargetDec = null;
            return verdict;
        }
        if (!slewing && _slewing) {
            _slewing = false;
            var aborted = _abortNoted;
            _abortNoted = false;
            return aborted
                ? new Verdict(Kind.None)
                : new Verdict(Kind.Completed, DurationSeconds: (TickMs() - _startedAtTickMs) / 1000.0);
        }
        return new Verdict(Kind.None);
    }

    /// <summary>Disconnect/reconnect: the episode state is meaningless across a connection.</summary>
    public void Reset() {
        _slewing = false;
        _abortNoted = false;
        _pendingTargetRa = null;
        _pendingTargetDec = null;
    }
}
