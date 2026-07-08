#region "copyright"

/*
    Copyright (c) 2026 Open Astro and the OpenAstro Ara contributors

    This file is part of OpenAstro Ara (forked from N.I.N.A.).

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using System.Text.Json;
using System.Text.Json.Nodes;

namespace OpenAstroAra.Server.Services;

/// <summary>
/// One per-filter flat block for <see cref="CalibrationSequenceBuilder.BuildMatchingFlatsBody"/>.
/// <paramref name="FilterName"/> null means the session had no filter wheel — the generated block
/// then has no SwitchFilter step. <paramref name="Gain"/> / <paramref name="Offset"/> are the
/// values the session's lights used (null = camera default, which TakeExposure expresses as its
/// -1 sentinel by simply omitting the property). <paramref name="FocuserPosition"/> restores the
/// per-filter focus the lights were captured at (§39.5: dust-mote shadows only align when the
/// optical train is unchanged).
/// </summary>
public sealed record FlatStepSpec(
    string? FilterName,
    int FrameCount,
    int TargetAdu,
    double TargetAduTolerancePct,
    int? Gain,
    int? Offset,
    int? FocuserPosition);

/// <summary>One per-filter §48.4 twilight sky-flat set: like <see cref="FlatStepSpec"/> plus the
/// stop window the drifting sky must stay inside.</summary>
public sealed record SkyFlatStepSpec(
    string? FilterName,
    int FrameCount,
    int TargetAdu,
    double TargetAduTolerancePct,
    double StopAtMaxAdu,
    double StopAtMinAdu,
    int? Gain,
    int? Offset,
    int? FocuserPosition);

/// <summary>The §48.4 sky-flat pointing + timing envelope (§48.7 sky_flat block): wait until the
/// sun rises through <paramref name="SunAltitudeDeg"/>, then slew to the flat-friendly patch at
/// <paramref name="AzimuthDeg"/>/<paramref name="AltitudeDeg"/>.</summary>
public sealed record SkyFlatEnvelope(
    double SunAltitudeDeg,
    double AzimuthDeg,
    double AltitudeDeg);

/// <summary>One (exposure, gain) dark set inside a temperature group. Null <paramref name="Gain"/>
/// = camera default (TakeExposure's -1 sentinel, expressed by omitting the property).</summary>
public sealed record DarkStepSpec(
    double ExposureSeconds,
    int? Gain,
    int FrameCount);

/// <summary>
/// One cooler set-point's worth of dark sets. Null <paramref name="TemperatureC"/> = capture at
/// ambient (no CoolCamera step — an uncooled camera, or the user didn't ask for regulation).
/// </summary>
public sealed record DarkTemperatureGroupSpec(
    double? TemperatureC,
    IReadOnlyList<DarkStepSpec> Steps);

/// <summary>One per-filter light block for the §40.6 resume-target synthesis: the session's
/// modal capture settings per filter, with <paramref name="FrameCount"/> = how many lights
/// that filter originally captured (a same-shape second pass on the target).</summary>
public sealed record LightStepSpec(
    string? FilterName,
    int FrameCount,
    double ExposureSeconds,
    int? Gain,
    int? Offset,
    int? FocuserPosition);

/// <summary>
/// §39.5/§39.8/§40.6 — materializes a capture plan into a runnable §38.1 sequence body.
/// Bodies use the NINA-verbatim <c>$type</c> tree (the same shape every stored sequence,
/// starter template, and NINA import uses); the sequencer's type remapping resolves them
/// to the OpenAstroAra classes on load, and the WILMA sequence renderer already knows how
/// to display them. One builder so the §39.8 dark-matrix generation (follow-up slice) and
/// the matching-flats generation share the exact same body dialect.
/// </summary>
public static class CalibrationSequenceBuilder {

    private const string SequentialContainerType = "NINA.Sequencer.Container.SequentialContainer, NINA.Sequencer";
    private const string SequentialStrategyType = "NINA.Sequencer.Container.ExecutionStrategy.SequentialStrategy, NINA.Sequencer";
    private const string LoopConditionType = "NINA.Sequencer.Conditions.LoopCondition, NINA.Sequencer";
    private const string SwitchFilterType = "NINA.Sequencer.SequenceItem.FilterWheel.SwitchFilter, NINA.Sequencer";
    private const string FilterInfoType = "NINA.Core.Model.Equipment.FilterInfo, NINA.Core";
    private const string MoveFocuserAbsoluteType = "NINA.Sequencer.SequenceItem.Focuser.MoveFocuserAbsolute, NINA.Sequencer";
    private const string TakeExposureType = "NINA.Sequencer.SequenceItem.Imaging.TakeExposure, NINA.Sequencer";
    private const string CoolCameraType = "NINA.Sequencer.SequenceItem.Camera.CoolCamera, NINA.Sequencer";
    // ARA-only classes still ride the NINA dialect: the loader's type remap is a plain
    // NINA→OpenAstroAra prefix replace, so these resolve to the OpenAstroAra classes on load.
    private const string FlatPanelFlatsType = "NINA.Sequencer.SequenceItem.FlatDevice.FlatPanelFlats, NINA.Sequencer";
    private const string SkyFlatsType = "NINA.Sequencer.SequenceItem.FlatDevice.SkyFlats, NINA.Sequencer";
    private const string ParkScopeType = "NINA.Sequencer.SequenceItem.Telescope.ParkScope, NINA.Sequencer";
    private const string WaitForSunAltitudeType = "NINA.Sequencer.SequenceItem.Utility.WaitForSunAltitude, NINA.Sequencer";
    private const string SlewScopeToAltAzType = "NINA.Sequencer.SequenceItem.Telescope.SlewScopeToAltAz, NINA.Sequencer";

    /// <summary>
    /// Build the §38.1 body: a SequentialContainer root with one per-filter container —
    /// [SwitchFilter → MoveFocuserAbsolute → FlatPanelFlats]. Since §48.3 the flat leaf is the
    /// auto-exposure instruction (it probes to the target ADU and captures its own FrameCount,
    /// so the per-filter block needs no LoopCondition), followed by an optional ParkScope when
    /// the §48.7 post_flat_park_mount policy asks for it.
    /// </summary>
    public static JsonElement BuildMatchingFlatsBody(string name, IReadOnlyList<FlatStepSpec> steps, bool parkMountAfter = false) {
        var items = new JsonArray();
        foreach (var step in steps) {
            items.Add(FlatBlock(step));
        }
        if (parkMountAfter) {
            items.Add(new JsonObject { ["$type"] = ParkScopeType });
        }

        var root = new JsonObject {
            ["schemaVersion"] = SequenceSchemaValidator.SchemaVersion,
            ["$type"] = SequentialContainerType,
            ["Strategy"] = Strategy(),
            ["Name"] = name,
            ["Conditions"] = new JsonArray(),
            ["Items"] = items,
            ["Triggers"] = new JsonArray(),
        };
        return JsonSerializer.SerializeToElement(root);
    }

    /// <summary>
    /// Build the §48.4 twilight sky-flats body: [WaitForSunAltitude (rising through the
    /// envelope's sun altitude) → SlewScopeToAltAz (the flat-friendly sky patch) → one
    /// per-filter SkyFlats block] + an optional trailing ParkScope. The sequence itself owns
    /// the twilight timing, which is what makes auto-starting it at run completion safe.
    /// </summary>
    public static JsonElement BuildSkyFlatsBody(string name, IReadOnlyList<SkyFlatStepSpec> steps, SkyFlatEnvelope envelope, bool parkMountAfter = false) {
        ArgumentNullException.ThrowIfNull(envelope);
        var items = new JsonArray {
            // Comparator LessThan waits WHILE the sun sits at-or-below the offset — i.e. until
            // it RISES through it (morning twilight; see WaitForSunAltitude.MustWait).
            new JsonObject {
                ["$type"] = WaitForSunAltitudeType,
                ["Data"] = new JsonObject {
                    ["Offset"] = envelope.SunAltitudeDeg,
                    ["Comparator"] = (int)OpenAstroAra.Core.Enums.ComparisonOperator.LessThan,
                },
            },
            // Whole-degree pointing (the §48.7 fields are degrees); minutes/seconds stay zero.
            new JsonObject {
                ["$type"] = SlewScopeToAltAzType,
                ["Coordinates"] = new JsonObject {
                    ["AzDegrees"] = (int)envelope.AzimuthDeg,
                    ["AltDegrees"] = (int)envelope.AltitudeDeg,
                },
            },
        };
        foreach (var step in steps) {
            items.Add(SkyFlatBlock(step));
        }
        if (parkMountAfter) {
            items.Add(new JsonObject { ["$type"] = ParkScopeType });
        }

        var root = new JsonObject {
            ["schemaVersion"] = SequenceSchemaValidator.SchemaVersion,
            ["$type"] = SequentialContainerType,
            ["Strategy"] = Strategy(),
            ["Name"] = name,
            ["Conditions"] = new JsonArray(),
            ["Items"] = items,
            ["Triggers"] = new JsonArray(),
        };
        return JsonSerializer.SerializeToElement(root);
    }

    /// <summary>One per-filter §48.4 sky-flat set: position the wheel + focuser, then hand the
    /// set to SkyFlats (per-frame re-probe + stop-window bail-outs — its own iteration).</summary>
    private static JsonObject SkyFlatBlock(SkyFlatStepSpec step) {
        var items = new JsonArray();

        if (step.FilterName is not null) {
            items.Add(SwitchFilterStep(step.FilterName));
        }

        if (step.FocuserPosition is int focus) {
            items.Add(MoveFocuserStep(focus));
        }

        var flats = new JsonObject {
            ["$type"] = SkyFlatsType,
            ["TargetAdu"] = step.TargetAdu,
            ["TargetAduTolerancePct"] = step.TargetAduTolerancePct,
            ["FrameCount"] = step.FrameCount,
            ["StopAtMaxAdu"] = step.StopAtMaxAdu,
            ["StopAtMinAdu"] = step.StopAtMinAdu,
        };
        if (step.Gain is int gain) {
            flats["Gain"] = gain;
        }
        if (step.Offset is int offset) {
            flats["Offset"] = offset;
        }
        items.Add(flats);

        var label = step.FilterName ?? "no filter";
        return new JsonObject {
            ["$type"] = SequentialContainerType,
            ["Strategy"] = Strategy(),
            ["Name"] = $"Sky flats — {label} ({step.FrameCount}× auto to {step.TargetAdu} ADU)",
            ["Conditions"] = new JsonArray(),
            ["Items"] = items,
            ["Triggers"] = new JsonArray(),
        };
    }

    /// <summary>The SwitchFilter step, resolving by NAME against the active profile's filter
    /// list (_position -1 on purpose: only a recorded position >= 0 wins as a slot-index
    /// fallback, and a stale index from a re-ordered wheel must not).</summary>
    private static JsonObject SwitchFilterStep(string filterName) => new() {
        ["$type"] = SwitchFilterType,
        ["Filter"] = new JsonObject {
            ["$type"] = FilterInfoType,
            ["_name"] = filterName,
            ["_position"] = -1,
        },
    };

    private static JsonObject MoveFocuserStep(int position) => new() {
        ["$type"] = MoveFocuserAbsoluteType,
        ["Position"] = position,
    };

    /// <summary>One per-filter §48.3 flat set: position the wheel + focuser, then hand the whole
    /// set to FlatPanelFlats (probe-to-ADU + N saved frames — its own iteration, no loop here).</summary>
    private static JsonObject FlatBlock(FlatStepSpec step) {
        var items = new JsonArray();

        if (step.FilterName is not null) {
            items.Add(SwitchFilterStep(step.FilterName));
        }

        if (step.FocuserPosition is int focus) {
            items.Add(MoveFocuserStep(focus));
        }

        var flats = new JsonObject {
            ["$type"] = FlatPanelFlatsType,
            ["TargetAdu"] = step.TargetAdu,
            ["TargetAduTolerancePct"] = step.TargetAduTolerancePct,
            ["FrameCount"] = step.FrameCount,
        };
        // Omitted Gain/Offset deserialize to FlatPanelFlats' -1 "camera default" sentinel.
        if (step.Gain is int gain) {
            flats["Gain"] = gain;
        }
        if (step.Offset is int offset) {
            flats["Offset"] = offset;
        }
        items.Add(flats);

        var label = step.FilterName ?? "no filter";
        return new JsonObject {
            ["$type"] = SequentialContainerType,
            ["Strategy"] = Strategy(),
            ["Name"] = $"Flats — {label} ({step.FrameCount}× auto to {step.TargetAdu} ADU)",
            ["Conditions"] = new JsonArray(),
            ["Items"] = items,
            ["Triggers"] = new JsonArray(),
        };
    }

    /// <summary>The shared per-filter capture shape (r1 dedup): a looped SequentialContainer of
    /// [SwitchFilter → MoveFocuserAbsolute → TakeExposure] — flats and resume-target lights
    /// differ only in ImageType and the block-name prefix.</summary>
    private static JsonObject FilterCaptureBlock(
            string namePrefix, string imageType, string? filterName, int frameCount,
            double exposureSeconds, int? gainValue, int? offsetValue, int? focuserPosition) {
        var items = new JsonArray();

        if (filterName is not null) {
            items.Add(SwitchFilterStep(filterName));
        }

        if (focuserPosition is int focus) {
            items.Add(MoveFocuserStep(focus));
        }

        var exposure = new JsonObject {
            ["$type"] = TakeExposureType,
            ["ExposureTime"] = exposureSeconds,
            ["ImageType"] = imageType,
            ["ExposureCount"] = 0,
        };
        // Omitted Gain/Offset deserialize to TakeExposure's -1 "camera default" sentinel.
        if (gainValue is int gain) {
            exposure["Gain"] = gain;
        }
        if (offsetValue is int offset) {
            exposure["Offset"] = offset;
        }
        items.Add(exposure);

        var label = filterName ?? "no filter";
        return new JsonObject {
            ["$type"] = SequentialContainerType,
            ["Strategy"] = Strategy(),
            ["Name"] = $"{namePrefix} — {label} ({frameCount}×{exposureSeconds:0.####}s)",
            ["Conditions"] = new JsonArray(new JsonObject {
                ["$type"] = LoopConditionType,
                ["Iterations"] = frameCount,
                ["CompletedIterations"] = 0,
            }),
            ["Items"] = items,
            ["Triggers"] = new JsonArray(),
        };
    }

    /// <summary>
    /// Build the §39.8 dark-matrix body: per temperature group, an optional CoolCamera step
    /// (Duration 0 = regulate as fast as the driver allows) followed by one looped container
    /// per (exposure, gain) combination shooting TakeExposure(DARK)s. Cover the scope or park
    /// first — darks depend only on the sensor, not the sky, which is why §39.8 recommends a
    /// cloudy/moonless night.
    /// </summary>
    public static JsonElement BuildDarkLibraryBody(string name, IReadOnlyList<DarkTemperatureGroupSpec> groups) {
        var items = new JsonArray();
        foreach (var group in groups) {
            if (group.TemperatureC is double temp) {
                items.Add(new JsonObject {
                    ["$type"] = CoolCameraType,
                    ["Temperature"] = temp,
                    ["Duration"] = 0,
                });
            }
            foreach (var step in group.Steps) {
                items.Add(DarkBlock(group.TemperatureC, step));
            }
        }

        var root = new JsonObject {
            ["schemaVersion"] = SequenceSchemaValidator.SchemaVersion,
            ["$type"] = SequentialContainerType,
            ["Strategy"] = Strategy(),
            ["Name"] = name,
            ["Conditions"] = new JsonArray(),
            ["Items"] = items,
            ["Triggers"] = new JsonArray(),
        };
        return JsonSerializer.SerializeToElement(root);
    }

    private static JsonObject DarkBlock(double? temperatureC, DarkStepSpec step) {
        var exposure = new JsonObject {
            ["$type"] = TakeExposureType,
            ["ExposureTime"] = step.ExposureSeconds,
            ["ImageType"] = "DARK",
            ["ExposureCount"] = 0,
        };
        if (step.Gain is int gain) {
            exposure["Gain"] = gain;
        }

        var gainLabel = step.Gain is int g ? $"g{g}" : "default gain";
        var tempLabel = temperatureC is double t ? $" @{t:0.#}°C" : "";
        return new JsonObject {
            ["$type"] = SequentialContainerType,
            ["Strategy"] = Strategy(),
            ["Name"] = $"Darks — {step.ExposureSeconds:0.####}s {gainLabel}{tempLabel} ({step.FrameCount}×)",
            ["Conditions"] = new JsonArray(new JsonObject {
                ["$type"] = LoopConditionType,
                ["Iterations"] = step.FrameCount,
                ["CompletedIterations"] = 0,
            }),
            ["Items"] = new JsonArray(exposure),
            ["Triggers"] = new JsonArray(),
        };
    }

    /// <summary>
    /// Build the §40.6 resume-target body: one looped per-filter container —
    /// [SwitchFilter → MoveFocuserAbsolute → TakeExposure(LIGHT)] × FrameCount — replaying
    /// the capture settings the session's lights actually used. The user adds the slew/center
    /// steps (per-frame plate-solve coordinates aren't in the catalog yet — see PORT_TODO).
    /// </summary>
    public static JsonElement BuildResumeTargetBody(string name, IReadOnlyList<LightStepSpec> steps) {
        var items = new JsonArray();
        foreach (var step in steps) {
            items.Add(LightBlock(step));
        }
        var root = new JsonObject {
            ["schemaVersion"] = SequenceSchemaValidator.SchemaVersion,
            ["$type"] = SequentialContainerType,
            ["Strategy"] = Strategy(),
            ["Name"] = name,
            ["Conditions"] = new JsonArray(),
            ["Items"] = items,
            ["Triggers"] = new JsonArray(),
        };
        return JsonSerializer.SerializeToElement(root);
    }

    private static JsonObject LightBlock(LightStepSpec step) =>
        FilterCaptureBlock("Lights", "LIGHT", step.FilterName, step.FrameCount,
            step.ExposureSeconds, step.Gain, step.Offset, step.FocuserPosition);

    private static JsonObject Strategy() => new() { ["$type"] = SequentialStrategyType };
}
