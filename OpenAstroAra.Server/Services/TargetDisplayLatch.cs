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

/// <summary>
/// §57.9 — decides whether the mount's TargetRA/TargetDec registers are shown or masked.
/// The target row is the mount's own register read, EXCEPT after a FindHome/Park: those are
/// not target commands, and the mount keeps the previous goto destination in its registers,
/// which would repaint the stale target forever. The latch is set by Home/Park, and released
/// by whichever comes first:
///
///  - an Ara-issued slew/sync (<see cref="NoteTargetCommand"/>),
///  - a reconnect (<see cref="Reset"/> — a fresh session trusts the registers again), or
///  - the registers CHANGING while latched — an external goto/sync (handset, ASIAIR app)
///    writes new registers, so the stale value the latch was hiding is gone.
///
/// Register change, not an observed slew-start, is deliberately the external-release trigger:
/// the Park/Find Home motion itself reads as an IsSlewing transition (§57.8 publishes Started
/// for it with null targets), so releasing on slew-start would re-show the stale registers
/// during the very park that latched them. An external goto to coordinates identical to the
/// stale registers stays masked — indistinguishable from no command, and it repaints nothing
/// wrong. Observed under the service's commit lock, like the other watches.
/// </summary>
internal sealed class TargetDisplayLatch {

    private bool _cleared;
    // The register snapshot the latch is hiding, captured on the first observation after the
    // latch arms (Park/Home run off the request thread, so command time has no fresh read;
    // the next 2 s poll tick does).
    private bool _staleKnown;
    private double? _staleTargetRa;
    private double? _staleTargetDec;

    /// <summary>An Ara-issued goto/sync — a real target command takes the display over.</summary>
    public void NoteTargetCommand() {
        _cleared = false;
        _staleKnown = false;
    }

    /// <summary>A FindHome/Park — hide the registers' stale goto destination.</summary>
    public void NoteNonTargetCommand() {
        _cleared = true;
        _staleKnown = false;
    }

    /// <summary>Disconnect/reconnect: a fresh session trusts the mount's registers again.</summary>
    public void Reset() {
        _cleared = false;
        _staleKnown = false;
    }

    /// <summary>
    /// Observe this poll tick's raw register read. Returns true when the target must be
    /// masked (nulled) in the published runtime, false when the register read is served as-is.
    /// </summary>
    public bool Observe(double? targetRaHours, double? targetDecDegrees) {
        if (!_cleared) {
            return false;
        }
        if (!_staleKnown) {
            _staleTargetRa = targetRaHours;
            _staleTargetDec = targetDecDegrees;
            _staleKnown = true;
            return true;
        }
        if (targetRaHours != _staleTargetRa || targetDecDegrees != _staleTargetDec) {
            // The registers moved under the latch — an external slew/sync set a new target.
            _cleared = false;
            return false;
        }
        return true;
    }
}
