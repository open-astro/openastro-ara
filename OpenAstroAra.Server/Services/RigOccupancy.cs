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

/// <summary>What a routine holds while it runs (#2141). A busy question names the holdings it cares about.</summary>
[Flags]
public enum RigHolding {
    None = 0,
    Mount = 1,
    MainCamera = 2,
    GuideCamera = 4,
    Focuser = 8,
    /// <summary>Writes frames to the storage drive.</summary>
    Disk = 16,
    Any = Mount | MainCamera | GuideCamera | Focuser | Disk,
}

/// <summary>
/// Rig Occupancy (#2141, ADR 0025): one register of what is running on the rig and what each routine holds.
/// A routine registers once, at composition, with a reading of its own running state; the register keeps no copy
/// of that state, so it cannot drift from the routine. Every busy question asks <see cref="Holder"/>.
/// </summary>
public sealed class RigOccupancy {
    /// <summary>The Session's name in a busy reason ("a sequence is running").</summary>
    public const string Session = "a sequence";

    // Filled once in the composition root, then only read; Register replaces the array whole.
    private volatile (string Name, RigHolding Holds, Func<bool> IsRunning)[] _routines = [];

    /// <summary>Adds a routine. <paramref name="name"/> reads in a reason: "{name} is running". The first running
    /// routine in registration order names the refusal, so register the widest one (the Session) first: a centering
    /// that a Session runs then reads as the Session.</summary>
    public RigOccupancy Register(string name, RigHolding holds, Func<bool> isRunning) {
        _routines = [.. _routines, (name, holds, isRunning)];
        return this;
    }

    /// <summary>The Session: a run that is not terminal (starting, running, paused, awaiting the person or
    /// aborting), or a catalog session still open. The catalog alone misses a run whose session failed to open.</summary>
    public RigOccupancy RegisterSession(ActiveRunSessionRegistry runs, Func<ISequencerService?> sequencer) =>
        Register(Session, RigHolding.Any, () => runs.HasAny || sequencer()?.HasUnfinishedRun == true);

    /// <summary>The name of the first running routine that holds any of <paramref name="holding"/>, or null when
    /// none does.</summary>
    public string? Holder(RigHolding holding = RigHolding.Any) {
        foreach (var (name, holds, isRunning) in _routines) {
            if ((holds & holding) != 0 && isRunning()) return name;
        }
        return null;
    }
}
