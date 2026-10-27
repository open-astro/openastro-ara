#region "copyright"
/*
    Copyright (c) 2026 Open Astro and the OpenAstro Ara contributors

    This file is part of OpenAstro Ara (forked from N.I.N.A.).

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/
#endregion "copyright"

using Microsoft.Extensions.Logging;
using OpenAstroAra.Equipment.Equipment.MyGuider.PHD2;
using OpenAstroAra.Server.Contracts;

namespace OpenAstroAra.Server.Services;

/// <summary>
/// #1448 — a Guiding Assistant Apply writes the guider's own profile. Ara owns these settings and pushes
/// them on every connect and tune (§63.5), so what Apply wrote must land in Ara's profile too: otherwise Ara
/// shows the old value while the guider runs the new one, and the next push quietly undoes the Apply.
/// </summary>
public sealed partial class GuiderService {

    /// <summary>Write the guider's post-Apply values into the active profile. The store is the source of
    /// truth (the in-memory settings hydrate from it on every Put); a headless service with no store sets
    /// the active profile's guider settings directly, and has nowhere to keep the advanced block.</summary>
    internal void RecordGuidingAssistantApplied(GuidingAssistantAppliedValues values) {
        if (_profileStore is null) {
            var live = _profileService.ActiveProfile.GuiderSettings;
            if (values.ExposureMs is > 0 and var ms) live.GuideExposureMs = ms;
            if (values.MinMoveRaPx is > 0 and var ra) live.MinimumMove = ra;
            if (values.MinMoveDecPx is > 0 and var dec) live.DecMinimumMove = dec;
            if (DecGuideModeFromGuider(values.DecGuideMode) is { } mode) live.DecGuideMode = mode;
        } else {
            var phd2 = _profileStore.GetPhd2Settings();
            var nextPhd2 = WithGuidingAssistantApplied(phd2, values);
            if (nextPhd2 != phd2) {
                _profileStore.PutPhd2Settings(nextPhd2);
            }
            var advanced = _profileStore.GetGuiderAdvancedSettings();
            var nextAdvanced = WithGuidingAssistantApplied(advanced, values);
            if (nextAdvanced != advanced) {
                _profileStore.PutGuiderAdvancedSettings(nextAdvanced);
            }
        }
        LogGuidingAssistantRecorded(values.ExposureMs, values.MinMoveRaPx, values.MinMoveDecPx, values.DecGuideMode,
            values.DecAlgorithm, values.BacklashEnabled, values.BacklashPulseMs);
    }

    /// <summary>The guider's Dec mode name ("North") → Ara's ("north"); null for anything else, so an
    /// unknown answer never overwrites the profile. Pure.</summary>
    internal static string? DecGuideModeFromGuider(string? guiderMode) => guiderMode?.Trim().ToUpperInvariant() switch {
        "AUTO" => "auto",
        "NORTH" => "north",
        "SOUTH" => "south",
        "OFF" => "off",
        _ => null,
    };

    /// <summary>The PHD2 block with the applied tuning values. A value the guider did not report, or one Ara
    /// would refuse at its write boundary (0 is "unset" for each of these), keeps the profile's. Pure.</summary>
    internal static Phd2SettingsDto WithGuidingAssistantApplied(Phd2SettingsDto p, GuidingAssistantAppliedValues v) => p with {
        GuideExposureMs = v.ExposureMs is > 0 and var ms ? ms : p.GuideExposureMs,
        MinimumMove = v.MinMoveRaPx is > 0 and var ra && double.IsFinite(ra) ? ra : p.MinimumMove,
        DecMinimumMove = v.MinMoveDecPx is > 0 and var dec && double.IsFinite(dec) ? dec : p.DecMinimumMove,
        DecGuideMode = DecGuideModeFromGuider(v.DecGuideMode) ?? p.DecGuideMode,
    };

    /// <summary>The advanced block with the applied Dec algorithm and backlash compensation. The backlash
    /// group is taken only with a pulse width inside the guider's range (the write boundary refuses an enable
    /// without one). Its floor and ceiling are the guider's when positive and ordered, else cleared (null =
    /// the guider keeps its own): an old pair kept beside a new pulse could fall outside the guider's
    /// [floor, pulse] check and fail every later push. Pure.</summary>
    internal static GuiderAdvancedSettingsDto WithGuidingAssistantApplied(GuiderAdvancedSettingsDto a, GuidingAssistantAppliedValues v) {
        var next = string.IsNullOrWhiteSpace(v.DecAlgorithm) ? a : a with { DecAlgorithm = v.DecAlgorithm.Trim() };
        if (v.BacklashPulseMs is int pulse
                && pulse is >= GuiderAdvancedSettingsMapping.BacklashPulseMinMs and <= GuiderAdvancedSettingsMapping.BacklashPulseMaxMs) {
            var bounds = v.BacklashFloorMs is > 0 and var floor && v.BacklashCeilingMs is > 0 and var ceiling && floor <= ceiling
                ? (Floor: (int?)floor, Ceiling: (int?)ceiling)
                : (Floor: null, Ceiling: null);
            next = next with {
                BacklashEnabled = v.BacklashEnabled ?? next.BacklashEnabled,
                BacklashPulseMs = pulse,
                BacklashFloorMs = bounds.Floor,
                BacklashCeilingMs = bounds.Ceiling,
            };
        }
        return next;
    }

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Guiding Assistant Apply recorded in Ara's profile: exposure {ExposureMs} ms, min move RA {MinMoveRa} / Dec {MinMoveDec} px, Dec mode {DecMode}, Dec algorithm {DecAlgorithm}, backlash {BacklashEnabled} {BacklashPulseMs} ms (#1448)")]
    private partial void LogGuidingAssistantRecorded(int? exposureMs, double? minMoveRa, double? minMoveDec, string? decMode,
        string? decAlgorithm, bool? backlashEnabled, int? backlashPulseMs);
}
