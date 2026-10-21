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
/// optical train is unchanged); <paramref name="RotatorMechanicalDeg"/> turns the camera back to
/// the lights' rotator angle for the same reason (null = no rotator on the lights).
/// <paramref name="TargetAdu"/> 0 = automatic: the set aims for <paramref name="TargetPercent"/>
/// of the camera's full scale and solves its own exposure and panel brightness.
/// </summary>
public sealed record FlatStepSpec(
    string? FilterName,
    int FrameCount,
    int TargetAdu,
    double TargetAduTolerancePct,
    int? Gain,
    int? Offset,
    int? FocuserPosition,
    double? RotatorMechanicalDeg = null,
    double TargetPercent = 50);

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
    int? FocuserPosition,
    double? RotatorMechanicalDeg = null);

/// <summary>One bias set: <paramref name="FrameCount"/> BIAS frames at the lights' gain, offset
/// and binning (null gain/offset = camera default). The camera takes them at its shortest
/// exposure; the step's own exposure is only a placeholder.</summary>
public sealed record BiasStepSpec(
    int? Gain,
    int? Offset,
    int Bin,
    int FrameCount);

/// <summary>The cooler around a flats run: cool to <paramref name="CoolToC"/> first (null = leave
/// the cooler alone), and warm the camera after when <paramref name="WarmAfter"/> — the flats
/// run is the end of the night, so it restores the imaging run's warm-up.</summary>
public sealed record FlatCooling(double? CoolToC, bool WarmAfter);

/// <summary>The §48.4 sky-flat pointing + timing envelope (§48.7 sky_flat block): wait until the
/// sun rises through <paramref name="SunAltitudeDeg"/>, then slew to the flat-friendly patch at
/// <paramref name="AzimuthDeg"/>/<paramref name="AltitudeDeg"/>.</summary>
public sealed record SkyFlatEnvelope(
    double SunAltitudeDeg,
    double AzimuthDeg,
    double AltitudeDeg);

/// <summary>One (exposure, gain) dark set inside a temperature group. Null <paramref name="Gain"/>
/// / <paramref name="Offset"/> = camera default (TakeExposure's -1 sentinel, expressed by omitting
/// the property); <paramref name="Bin"/> above 1 sets the binning.</summary>
public sealed record DarkStepSpec(
    double ExposureSeconds,
    int? Gain,
    int FrameCount,
    int? Offset = null,
    int Bin = 1);

/// <summary>The bias and dark sets a calibration run shoots after its flats. With
/// <paramref name="LensCapPause"/> the run first opens the panel's cover and waits for the user
/// to put the lens cap on (a panel doesn't always seal out stray light).</summary>
public sealed record AfterFlatsSpec(
    IReadOnlyList<BiasStepSpec> Bias,
    IReadOnlyList<DarkStepSpec> Darks,
    bool LensCapPause);

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
/// Bodies use the <c>$type</c> tree every stored sequence uses, with the OpenAstroAra type
/// names the client's sequence editor builds (its instruction catalog keys on them, so a
/// NINA-named step would show as an unknown "?" row; the loader accepts both). One builder so the §39.8 dark-matrix generation (follow-up slice) and
/// the matching-flats generation share the exact same body dialect.
/// </summary>
public static class CalibrationSequenceBuilder {

    private const string SequentialContainerType = "OpenAstroAra.Sequencer.Container.SequentialContainer, OpenAstroAra.Sequencer";
    private const string SequentialStrategyType = "OpenAstroAra.Sequencer.Container.ExecutionStrategy.SequentialStrategy, OpenAstroAra.Sequencer";
    private const string LoopConditionType = "OpenAstroAra.Sequencer.Conditions.LoopCondition, OpenAstroAra.Sequencer";
    private const string SwitchFilterType = "OpenAstroAra.Sequencer.SequenceItem.FilterWheel.SwitchFilter, OpenAstroAra.Sequencer";
    private const string FilterInfoType = "OpenAstroAra.Core.Model.Equipment.FilterInfo, OpenAstroAra.Core";
    private const string MoveFocuserAbsoluteType = "OpenAstroAra.Sequencer.SequenceItem.Focuser.MoveFocuserAbsolute, OpenAstroAra.Sequencer";
    private const string TakeExposureType = "OpenAstroAra.Sequencer.SequenceItem.Imaging.TakeExposure, OpenAstroAra.Sequencer";
    private const string CoolCameraType = "OpenAstroAra.Sequencer.SequenceItem.Camera.CoolCamera, OpenAstroAra.Sequencer";
    private const string WarmCameraType = "OpenAstroAra.Sequencer.SequenceItem.Camera.WarmCamera, OpenAstroAra.Sequencer";
    private const string MoveRotatorMechanicalType = "OpenAstroAra.Sequencer.SequenceItem.Rotator.MoveRotatorMechanical, OpenAstroAra.Sequencer";
    private const string FlatPanelFlatsType = "OpenAstroAra.Sequencer.SequenceItem.FlatDevice.FlatPanelFlats, OpenAstroAra.Sequencer";
    private const string SkyFlatsType = "OpenAstroAra.Sequencer.SequenceItem.FlatDevice.SkyFlats, OpenAstroAra.Sequencer";
    private const string ParkScopeType = "OpenAstroAra.Sequencer.SequenceItem.Telescope.ParkScope, OpenAstroAra.Sequencer";
    private const string WaitForSunAltitudeType = "OpenAstroAra.Sequencer.SequenceItem.Utility.WaitForSunAltitude, OpenAstroAra.Sequencer";
    private const string SlewScopeToAltAzType = "OpenAstroAra.Sequencer.SequenceItem.Telescope.SlewScopeToAltAz, OpenAstroAra.Sequencer";
    private const string OpenFlatPanelCoverType = "OpenAstroAra.Sequencer.SequenceItem.FlatDevice.OpenFlatPanelCover, OpenAstroAra.Sequencer";
    private const string WaitForUserType = "OpenAstroAra.Sequencer.SequenceItem.Utility.WaitForUser, OpenAstroAra.Sequencer";

    /// <summary>What the lens-cap pause asks the user to do.</summary>
    internal const string LensCapPrompt = "Put the lens cap on the scope (move the flat panel out of the way if its cover didn't open), then press Resume to take the bias and darks.";

    /// <summary>
    /// Build the §38.1 body: a SequentialContainer root with one per-filter container —
    /// [SwitchFilter → MoveFocuserAbsolute → FlatPanelFlats]. Since §48.3 the flat leaf is the
    /// auto-exposure instruction (it probes to the target ADU and captures its own FrameCount,
    /// so the per-filter block needs no LoopCondition), followed by an optional ParkScope when
    /// the §48.7 post_flat_park_mount policy asks for it. <paramref name="cooling"/> wraps the
    /// blocks in a CoolCamera / WarmCamera pair. <paramref name="afterFlats"/> bias and dark sets
    /// run after the flats: the panel is off by then and the cover still closed over the optics,
    /// or the user has put the lens cap on.
    /// </summary>
    public static JsonElement BuildMatchingFlatsBody(string name, IReadOnlyList<FlatStepSpec> steps, bool parkMountAfter = false, FlatCooling? cooling = null, AfterFlatsSpec? afterFlats = null) {
        var items = new JsonArray();
        AddCoolStep(items, cooling);
        foreach (var step in steps) {
            items.Add(FlatBlock(step));
        }
        AddAfterFlats(items, afterFlats);
        if (parkMountAfter) {
            items.Add(new JsonObject { ["$type"] = ParkScopeType });
        }
        AddWarmStep(items, cooling);

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
    public static JsonElement BuildSkyFlatsBody(string name, IReadOnlyList<SkyFlatStepSpec> steps, SkyFlatEnvelope envelope, bool parkMountAfter = false, FlatCooling? cooling = null, AfterFlatsSpec? afterFlats = null) {
        ArgumentNullException.ThrowIfNull(envelope);
        var items = new JsonArray();
        // Cool before the twilight wait, so the sensor is at temperature when the sky is ready.
        AddCoolStep(items, cooling);
        // Comparator LessThan waits WHILE the sun sits at-or-below the offset — i.e. until
        // it RISES through it (morning twilight; see WaitForSunAltitude.MustWait).
        items.Add(new JsonObject {
            ["$type"] = WaitForSunAltitudeType,
            ["Data"] = new JsonObject {
                ["Offset"] = envelope.SunAltitudeDeg,
                ["Comparator"] = (int)OpenAstroAra.Core.Enums.ComparisonOperator.LessThan,
            },
        });
        // Whole-degree pointing (the §48.7 fields are degrees); minutes/seconds stay zero.
        items.Add(new JsonObject {
            ["$type"] = SlewScopeToAltAzType,
            ["Coordinates"] = new JsonObject {
                ["AzDegrees"] = (int)envelope.AzimuthDeg,
                ["AltDegrees"] = (int)envelope.AltitudeDeg,
            },
        });
        foreach (var step in steps) {
            items.Add(SkyFlatBlock(step));
        }
        // Darks need the scope covered after sky flats, so these runs want the lens-cap pause.
        AddAfterFlats(items, afterFlats);
        if (parkMountAfter) {
            items.Add(new JsonObject { ["$type"] = ParkScopeType });
        }
        AddWarmStep(items, cooling);

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

        if (step.RotatorMechanicalDeg is double angle) {
            items.Add(MoveRotatorStep(angle));
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

    private static JsonObject MoveRotatorStep(double mechanicalDeg) => new() {
        ["$type"] = MoveRotatorMechanicalType,
        ["MechanicalPosition"] = mechanicalDeg,
    };

    // Duration 0 = regulate as fast as the driver allows (the dark library's convention): the
    // camera is usually still at temperature from the night's run, and a fixed ramp would stall.
    private static void AddCoolStep(JsonArray items, FlatCooling? cooling) {
        if (cooling?.CoolToC is double temp) {
            items.Add(new JsonObject {
                ["$type"] = CoolCameraType,
                ["Temperature"] = temp,
                ["Duration"] = 0,
            });
        }
    }

    private static void AddWarmStep(JsonArray items, FlatCooling? cooling) {
        if (cooling is { CoolToC: not null, WarmAfter: true }) {
            items.Add(new JsonObject {
                ["$type"] = WarmCameraType,
                ["Duration"] = 0,
            });
        }
    }

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

        if (step.RotatorMechanicalDeg is double angle) {
            items.Add(MoveRotatorStep(angle));
        }

        var flats = new JsonObject {
            ["$type"] = FlatPanelFlatsType,
            ["TargetAdu"] = step.TargetAdu,
            ["TargetPercent"] = step.TargetPercent,
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
            ["Name"] = step.TargetAdu > 0
                ? $"Flats — {label} ({step.FrameCount}× auto to {step.TargetAdu} ADU)"
                : $"Flats — {label} ({step.FrameCount}× auto to {step.TargetPercent:0.#}% of range)",
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

    /// <summary>The placeholder exposure a generated bias step carries; the camera replaces it
    /// with its shortest exposure (TakeExposure rejects a zero exposure).</summary>
    internal const double BiasPlaceholderExposureSec = 0.001;

    private static void AddAfterFlats(JsonArray items, AfterFlatsSpec? afterFlats) {
        if (afterFlats is null || (afterFlats.Bias.Count == 0 && afterFlats.Darks.Count == 0)) {
            return;
        }
        if (afterFlats.LensCapPause) {
            items.Add(new JsonObject { ["$type"] = OpenFlatPanelCoverType });
            items.Add(new JsonObject { ["$type"] = WaitForUserType, ["Text"] = LensCapPrompt });
        }
        foreach (var step in afterFlats.Bias) {
            items.Add(BiasBlock(step));
        }
        foreach (var step in afterFlats.Darks) {
            items.Add(DarkBlock(temperatureC: null, step));
        }
    }

    private static JsonObject BiasBlock(BiasStepSpec step) {
        var exposure = new JsonObject {
            ["$type"] = TakeExposureType,
            ["ExposureTime"] = BiasPlaceholderExposureSec,
            ["ImageType"] = "BIAS",
            ["ExposureCount"] = 0,
        };
        if (step.Gain is int gain) {
            exposure["Gain"] = gain;
        }
        if (step.Offset is int offset) {
            exposure["Offset"] = offset;
        }
        if (step.Bin > 1) {
            exposure["Binning"] = new JsonObject { ["X"] = step.Bin, ["Y"] = step.Bin };
        }

        var gainLabel = step.Gain is int g ? $"g{g}" : "default gain";
        var offsetLabel = step.Offset is int o ? $" o{o}" : "";
        var binLabel = step.Bin > 1 ? $" bin{step.Bin}" : "";
        return new JsonObject {
            ["$type"] = SequentialContainerType,
            ["Strategy"] = Strategy(),
            ["Name"] = $"Bias — {gainLabel}{offsetLabel}{binLabel} ({step.FrameCount}×)",
            ["Conditions"] = new JsonArray(new JsonObject {
                ["$type"] = LoopConditionType,
                ["Iterations"] = step.FrameCount,
                ["CompletedIterations"] = 0,
            }),
            ["Items"] = new JsonArray(exposure),
            ["Triggers"] = new JsonArray(),
        };
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
        if (step.Offset is int offset) {
            exposure["Offset"] = offset;
        }
        if (step.Bin > 1) {
            exposure["Binning"] = new JsonObject { ["X"] = step.Bin, ["Y"] = step.Bin };
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
    /// steps (per-frame plate-solve coordinates aren't in the catalog yet — see the P1–P5 issue backlog).
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
