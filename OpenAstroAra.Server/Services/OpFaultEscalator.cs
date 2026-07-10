#region "copyright"

/*
    Copyright (c) 2026 Open Astro and the OpenAstro Ara contributors

    This file is part of OpenAstro Ara (forked from N.I.N.A.).

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using OpenAstroAra.Server.Contracts;
using System;
using System.Collections.Generic;

namespace OpenAstroAra.Server.Services;

/// <summary>
/// §42.2 (audit items 2+5) — decides when a device's op-channel faults have become PERSISTENT.
/// A single stalled slew or errored capture is instruction-level news (the §42.4 fault +
/// <c>Attempts</c> retries own it); the same device failing over and over is equipment-level
/// news that deserves the matrix's escalation row ("Retry → Abort + park" for the mount,
/// "Pause if persistent" for the camera). This class is only the counter: a sliding
/// wall-clock window of definite op failures (<see cref="EquipmentFaultKind.StallTimeout"/> /
/// <see cref="EquipmentFaultKind.OpError"/> — advisory kinds like value_mismatch/cooling_drift
/// have their own notify-only rows) per device type; reaching
/// <see cref="DefaultThreshold"/> within <see cref="DefaultWindow"/> fires once and restarts
/// that device's count, so continued failures must earn a full fresh streak before escalating
/// again. What the escalation DOES is <see cref="FaultPolicyMatrix"/>'s decision.
///
/// Not internally synchronized — the owning FaultReactionService mutates it under its gate.
/// </summary>
public sealed class OpFaultEscalator {

    /// <summary>Definite op failures within the window before escalation. Three failures is
    /// at least one fully retried instruction (or three separate ones) — past bad luck.</summary>
    public const int DefaultThreshold = 3;

    /// <summary>Sliding window — op faults older than this no longer count toward
    /// persistence (a failed slew at dusk shouldn't arm a hair-trigger for midnight).</summary>
    public static readonly TimeSpan DefaultWindow = TimeSpan.FromMinutes(10);

    private readonly int _threshold;
    private readonly TimeSpan _window;
    private readonly Dictionary<DeviceType, List<DateTimeOffset>> _recent = [];

    public OpFaultEscalator(int threshold = DefaultThreshold, TimeSpan? window = null) {
        _threshold = threshold < 1 ? 1 : threshold;
        _window = window ?? DefaultWindow;
    }

    /// <summary>One call per published fault. Returns true when THIS fault tips the device
    /// over the persistence threshold — the caller escalates this fault's episode instead of
    /// running the plain notify. Kinds that aren't definite op failures never count.</summary>
    public bool Observe(DeviceType deviceType, EquipmentFaultKind kind, DateTimeOffset nowUtc) {
        if (kind is not (EquipmentFaultKind.StallTimeout or EquipmentFaultKind.OpError)) {
            return false;
        }
        if (!_recent.TryGetValue(deviceType, out var stamps)) {
            stamps = [];
            _recent[deviceType] = stamps;
        }
        stamps.RemoveAll(t => nowUtc - t >= _window);
        stamps.Add(nowUtc);
        if (stamps.Count >= _threshold) {
            stamps.Clear(); // fired — a repeat escalation needs a full fresh streak
            return true;
        }
        return false;
    }
}
