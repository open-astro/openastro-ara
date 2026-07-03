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
    double ExposureSeconds,
    int? Gain,
    int? Offset,
    int? FocuserPosition);

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

    /// <summary>
    /// Build the §38.1 body: a SequentialContainer root with one looped per-filter container —
    /// [SwitchFilter → MoveFocuserAbsolute → TakeExposure(FLAT)] × FrameCount. SwitchFilter /
    /// MoveFocuserAbsolute re-execute on every loop pass; both are no-ops once in position,
    /// which is exactly how NINA's own SmartExposure loops behave.
    /// </summary>
    public static JsonElement BuildMatchingFlatsBody(string name, IReadOnlyList<FlatStepSpec> steps) {
        var items = new JsonArray();
        foreach (var step in steps) {
            items.Add(FlatBlock(step));
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

    private static JsonObject FlatBlock(FlatStepSpec step) {
        var items = new JsonArray();

        if (step.FilterName is not null) {
            items.Add(new JsonObject {
                ["$type"] = SwitchFilterType,
                // _position -1 on purpose: SwitchFilter.MatchFilter resolves by NAME against the
                // active profile's filter list, and only falls back to a slot index when the
                // recorded position is >= 0. A stale index from a re-ordered wheel must not win.
                ["Filter"] = new JsonObject {
                    ["$type"] = FilterInfoType,
                    ["_name"] = step.FilterName,
                    ["_position"] = -1,
                },
            });
        }

        if (step.FocuserPosition is int focus) {
            items.Add(new JsonObject {
                ["$type"] = MoveFocuserAbsoluteType,
                ["Position"] = focus,
            });
        }

        var exposure = new JsonObject {
            ["$type"] = TakeExposureType,
            ["ExposureTime"] = step.ExposureSeconds,
            ["ImageType"] = "FLAT",
            ["ExposureCount"] = 0,
        };
        // Omitted Gain/Offset deserialize to TakeExposure's -1 "camera default" sentinel.
        if (step.Gain is int gain) {
            exposure["Gain"] = gain;
        }
        if (step.Offset is int offset) {
            exposure["Offset"] = offset;
        }
        items.Add(exposure);

        var label = step.FilterName ?? "no filter";
        return new JsonObject {
            ["$type"] = SequentialContainerType,
            ["Strategy"] = Strategy(),
            ["Name"] = $"Flats — {label} ({step.FrameCount}×{step.ExposureSeconds:0.####}s)",
            ["Conditions"] = new JsonArray(new JsonObject {
                ["$type"] = LoopConditionType,
                ["Iterations"] = step.FrameCount,
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

    private static JsonObject LightBlock(LightStepSpec step) {
        var items = new JsonArray();
        if (step.FilterName is not null) {
            items.Add(new JsonObject {
                ["$type"] = SwitchFilterType,
                // _position -1: resolve by NAME only (same rationale as FlatBlock).
                ["Filter"] = new JsonObject {
                    ["$type"] = FilterInfoType,
                    ["_name"] = step.FilterName,
                    ["_position"] = -1,
                },
            });
        }
        if (step.FocuserPosition is int focus) {
            items.Add(new JsonObject {
                ["$type"] = MoveFocuserAbsoluteType,
                ["Position"] = focus,
            });
        }
        var exposure = new JsonObject {
            ["$type"] = TakeExposureType,
            ["ExposureTime"] = step.ExposureSeconds,
            ["ImageType"] = "LIGHT",
            ["ExposureCount"] = 0,
        };
        if (step.Gain is int gain) {
            exposure["Gain"] = gain;
        }
        if (step.Offset is int offset) {
            exposure["Offset"] = offset;
        }
        items.Add(exposure);

        var label = step.FilterName ?? "no filter";
        return new JsonObject {
            ["$type"] = SequentialContainerType,
            ["Strategy"] = Strategy(),
            ["Name"] = $"Lights — {label} ({step.FrameCount}×{step.ExposureSeconds:0.####}s)",
            ["Conditions"] = new JsonArray(new JsonObject {
                ["$type"] = LoopConditionType,
                ["Iterations"] = step.FrameCount,
                ["CompletedIterations"] = 0,
            }),
            ["Items"] = items,
            ["Triggers"] = new JsonArray(),
        };
    }

    private static JsonObject Strategy() => new() { ["$type"] = SequentialStrategyType };
}
