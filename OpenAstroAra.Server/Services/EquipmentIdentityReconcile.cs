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

namespace OpenAstroAra.Server.Services;

/// <summary>#1362 — the gear slots whose Alpaca identity the profile tracks. One per physical role; the
/// guide camera is a role of its own because it is not an Ara device service (the guider daemon opens it)
/// and the token names the profile key (<c>guidecamera</c>).</summary>
public enum GearSlot {
    Camera, GuideCamera, Telescope, Focuser, FilterWheel, Rotator, Dome, Switch, SafetyMonitor, ObservingConditions, CoverCalibrator,
}

public enum EquipmentIdentityOutcome {
    /// <summary>The slot's identity is the stored one (or there was nothing usable to compare): nothing to do.</summary>
    Unchanged,
    /// <summary>Nothing was stored for the slot yet: record it quietly (a fresh profile, a first connect, a pre-#1362 file).</summary>
    FirstSeen,
    /// <summary>A different non-empty UniqueID answers in the slot: the gear changed.</summary>
    Changed,
}

/// <summary>Pure half of the #1362 reconcile: what a slot's live identity means against the stored one.</summary>
public static class EquipmentIdentityReconcile {

    /// <summary>The profile key for a slot (<c>filterwheel</c>, <c>guidecamera</c>, …).</summary>
    public static string Key(GearSlot slot) => slot.ToString().ToLowerInvariant();

    /// <summary>The slot an Ara device service's type reports into, or null for a type the profile does not track
    /// (the guider is a daemon, not an Alpaca device; its camera is <see cref="GearSlot.GuideCamera"/>).</summary>
    public static GearSlot? SlotOf(DeviceType type) => type.Canonical() switch {
        DeviceType.Camera => GearSlot.Camera,
        DeviceType.Telescope => GearSlot.Telescope,
        DeviceType.Focuser => GearSlot.Focuser,
        DeviceType.FilterWheel => GearSlot.FilterWheel,
        DeviceType.Rotator => GearSlot.Rotator,
        DeviceType.Dome => GearSlot.Dome,
        DeviceType.Switch => GearSlot.Switch,
        DeviceType.SafetyMonitor => GearSlot.SafetyMonitor,
        DeviceType.ObservingConditions => GearSlot.ObservingConditions,
        DeviceType.CoverCalibrator => GearSlot.CoverCalibrator,
        _ => null,
    };

    /// <summary>The user-facing name of a slot for the inbox title ("Guide camera changed").</summary>
    public static string Label(GearSlot slot) => slot switch {
        GearSlot.Camera => "Camera",
        GearSlot.GuideCamera => "Guide camera",
        GearSlot.Telescope => "Mount",
        GearSlot.Focuser => "Focuser",
        GearSlot.FilterWheel => "Filter wheel",
        GearSlot.Rotator => "Rotator",
        GearSlot.Dome => "Dome",
        GearSlot.Switch => "Switch",
        GearSlot.SafetyMonitor => "Safety monitor",
        GearSlot.ObservingConditions => "Weather station",
        GearSlot.CoverCalibrator => "Flat panel",
        _ => slot.ToString(),
    };

    /// <summary>The management API's <c>configureddevices</c> key for an Ara device.</summary>
    public static string DeviceKey(DeviceType type, int deviceNumber) =>
        $"{type.Canonical().ToString().ToLowerInvariant()}/{deviceNumber.ToString(System.Globalization.CultureInfo.InvariantCulture)}";

    /// <summary>Compare the stored identity with what the slot answers now. A blank live id (a bridge that
    /// reports none, an unreachable management API) decides nothing: the profile keeps what it has.</summary>
    public static EquipmentIdentityOutcome Decide(DeviceIdentityDto? stored, AlpacaConfiguredDevice? live) {
        var liveId = (live?.UniqueId ?? string.Empty).Trim();
        if (liveId.Length == 0) {
            return EquipmentIdentityOutcome.Unchanged;
        }
        var storedId = (stored?.UniqueId ?? string.Empty).Trim();
        if (storedId.Length == 0) {
            return EquipmentIdentityOutcome.FirstSeen;
        }
        return string.Equals(storedId, liveId, StringComparison.Ordinal)
            ? EquipmentIdentityOutcome.Unchanged
            : EquipmentIdentityOutcome.Changed;
    }

    /// <summary>What a swap in a slot invalidates and what the user should re-check: the derived values
    /// that were measured through the old device, and the hand-entered fields the driver cannot report.</summary>
    public static string ChangeAdvice(GearSlot slot) => slot switch {
        GearSlot.Camera => "Sensor size and pixel size were re-read from the driver. Check the focal length and reducer under Settings → Optics. "
            + "The Smart Focus, unguided-tracking and sky-model calibrations were cleared; they will be measured again on the next run.",
        GearSlot.GuideCamera => "The guide pixel size was re-read from the driver. Check the guide focal length under Settings → Guiding. "
            + "The guider's dark library and defect map need rebuilding, and its calibration was cleared.",
        GearSlot.Telescope => "The unguided-tracking calibration was cleared and the guider's calibration was reset; both are measured again on the next run. "
            + "Check the mount's spec-sheet prior under Settings → Unguided.",
        GearSlot.Focuser => "The Smart Focus calibration (step size, backlash) was cleared and will be measured again on the next focus run.",
        GearSlot.FilterWheel => "The filter names were re-read from the wheel. Check the per-filter focus offsets under Settings → Filters.",
        GearSlot.Rotator => "Check the rotator's reverse setting and sky-angle offset before the next framing.",
        _ => "Check the device's settings before the next run.",
    };
}
