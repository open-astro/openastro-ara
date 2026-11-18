#region "copyright"

/*
    Copyright (c) 2026 Open Astro and the OpenAstro Ara contributors

    This file is part of OpenAstro Ara (forked from N.I.N.A.).

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using OpenAstroAra.Sequencer.Conditions;
using OpenAstroAra.Sequencer.Container;
using OpenAstroAra.Sequencer.SequenceItem.Autofocus;
using OpenAstroAra.Sequencer.SequenceItem.Camera;
using OpenAstroAra.Sequencer.SequenceItem.Connect;
using OpenAstroAra.Sequencer.SequenceItem.Dome;
using OpenAstroAra.Sequencer.SequenceItem.FilterWheel;
using OpenAstroAra.Sequencer.SequenceItem.FlatDevice;
using OpenAstroAra.Sequencer.SequenceItem.Focuser;
using OpenAstroAra.Sequencer.SequenceItem.Guider;
using OpenAstroAra.Sequencer.SequenceItem.Imaging;
using OpenAstroAra.Sequencer.SequenceItem.Platesolving;
using OpenAstroAra.Sequencer.SequenceItem.Rotator;
using OpenAstroAra.Sequencer.SequenceItem.SafetyMonitor;
using OpenAstroAra.Sequencer.SequenceItem.Switch;
using OpenAstroAra.Sequencer.SequenceItem.Telescope;
using OpenAstroAra.Sequencer.SequenceItem.Utility;
using OpenAstroAra.Sequencer.Trigger.Autofocus;
using OpenAstroAra.Sequencer.Trigger.Connect;
using OpenAstroAra.Sequencer.Trigger.Guider;
using OpenAstroAra.Sequencer.Trigger.MeridianFlip;
using OpenAstroAra.Sequencer.Trigger.Platesolving;
using OpenAstroAra.Sequencer.Trigger.Telescope;
using System;
using System.Collections.Generic;
using System.Linq;

namespace OpenAstroAra.Sequencer.Format;

/// <summary>Which engine list a node of a v2 document sits in, and so which engine class family builds it.</summary>
public enum SequenceNodeRole {
    Container,
    Instruction,
    Condition,
    Trigger,
}

/// <summary>
/// One row of the kind table: the v2 <c>kind</c>, the engine class it builds and the list that class
/// is registered in. <paramref name="NeedsAstrometryNatives"/> marks the two prototypes whose constructor
/// runs a NOVAS transform, which a daemon without the astrometry natives cannot build (it skips them).
/// </summary>
public sealed record SequenceKindEntry(string Kind, Type EngineClass, SequenceNodeRole Role, bool NeedsAstrometryNatives = false);

/// <summary>
/// The one place a v2 <c>kind</c> string meets an engine class (#2025). Declared in the order
/// <c>HeadlessSequencerFactory.WithDefaults</c> registers the classes, which builds each list from it.
/// <c>unknown</c> (four classes, picked by <c>role</c>) and <c>glover-exposure</c> (no class yet) are not entries.
/// </summary>
public static class SequenceKinds {

    /// <summary>The kind of the document's one root node.</summary>
    public const string Root = "root";

    /// <summary>The placeholder for something Ara cannot run.</summary>
    public const string Unknown = "unknown";

    /// <summary>The Ara-only adaptive exposure; it has no engine class until follow-up (d).</summary>
    public const string GloverExposure = "glover-exposure";

    public static IReadOnlyList<SequenceKindEntry> All { get; } = new SequenceKindEntry[] {
            new("root", typeof(SequenceRootContainer), SequenceNodeRole.Container),
            new("sequential", typeof(SequentialContainer), SequenceNodeRole.Container),
            new("parallel", typeof(ParallelContainer), SequenceNodeRole.Container),
            new("target", typeof(DeepSkyObjectContainer), SequenceNodeRole.Container),
            new("smart-exposure", typeof(SmartExposure), SequenceNodeRole.Container),
            new("annotation", typeof(Annotation), SequenceNodeRole.Instruction),
            new("wait-for-user", typeof(WaitForUser), SequenceNodeRole.Instruction),
            new("wait-for-duration", typeof(WaitForTimeSpan), SequenceNodeRole.Instruction),
            new("wait-for-time", typeof(WaitForTime), SequenceNodeRole.Instruction),
            new("wait-for-sun-altitude", typeof(WaitForSunAltitude), SequenceNodeRole.Instruction),
            new("wait-until-safe", typeof(WaitUntilSafe), SequenceNodeRole.Instruction),
            new("set-tracking", typeof(SetTracking), SequenceNodeRole.Instruction),
            new("unpark-mount", typeof(UnparkScope), SequenceNodeRole.Instruction),
            new("park-mount", typeof(ParkScope), SequenceNodeRole.Instruction),
            new("find-home-mount", typeof(FindHome), SequenceNodeRole.Instruction),
            new("slew-radec", typeof(SlewScopeToRaDec), SequenceNodeRole.Instruction),
            new("slew-altaz", typeof(SlewScopeToAltAz), SequenceNodeRole.Instruction),
            new("move-focuser-absolute", typeof(MoveFocuserAbsolute), SequenceNodeRole.Instruction),
            new("move-focuser-relative", typeof(MoveFocuserRelative), SequenceNodeRole.Instruction),
            new("move-focuser-by-temperature", typeof(MoveFocuserByTemperature), SequenceNodeRole.Instruction),
            new("capture", typeof(TakeExposure), SequenceNodeRole.Instruction),
            new("cool-camera", typeof(CoolCamera), SequenceNodeRole.Instruction),
            new("warm-camera", typeof(WarmCamera), SequenceNodeRole.Instruction),
            new("set-usb-limit", typeof(SetUSBLimit), SequenceNodeRole.Instruction),
            new("set-readout-mode", typeof(SetReadoutMode), SequenceNodeRole.Instruction),
            new("dew-heater", typeof(DewHeater), SequenceNodeRole.Instruction),
            new("start-guiding", typeof(StartGuiding), SequenceNodeRole.Instruction),
            new("stop-guiding", typeof(StopGuiding), SequenceNodeRole.Instruction),
            new("dither", typeof(Dither), SequenceNodeRole.Instruction),
            new("move-rotator-mechanical", typeof(MoveRotatorMechanical), SequenceNodeRole.Instruction),
            new("set-switch-value", typeof(SetSwitchValue), SequenceNodeRole.Instruction),
            new("open-dome-shutter", typeof(OpenDomeShutter), SequenceNodeRole.Instruction),
            new("close-dome-shutter", typeof(CloseDomeShutter), SequenceNodeRole.Instruction),
            new("park-dome", typeof(ParkDome), SequenceNodeRole.Instruction),
            new("find-home-dome", typeof(FindHomeDome), SequenceNodeRole.Instruction),
            new("slew-dome-azimuth", typeof(SlewDomeAzimuth), SequenceNodeRole.Instruction),
            new("enable-dome-sync", typeof(EnableDomeSynchronization), SequenceNodeRole.Instruction),
            new("disable-dome-sync", typeof(DisableDomeSynchronization), SequenceNodeRole.Instruction),
            new("sync-dome", typeof(SynchronizeDome), SequenceNodeRole.Instruction),
            new("switch-filter", typeof(SwitchFilter), SequenceNodeRole.Instruction),
            new("run-autofocus", typeof(RunAutofocus), SequenceNodeRole.Instruction),
            new("center-and-rotate", typeof(CenterAndRotate), SequenceNodeRole.Instruction),
            new("tracking-probe", typeof(TrackingProbe), SequenceNodeRole.Instruction),
            new("flat-panel-flats", typeof(FlatPanelFlats), SequenceNodeRole.Instruction),
            new("sky-flats", typeof(SkyFlats), SequenceNodeRole.Instruction),
            new("open-flat-panel-cover", typeof(OpenFlatPanelCover), SequenceNodeRole.Instruction),
            new("close-flat-panel-cover", typeof(CloseFlatPanelCover), SequenceNodeRole.Instruction),
            new("connect-all-equipment", typeof(ConnectAllEquipment), SequenceNodeRole.Instruction),
            new("connect-equipment", typeof(ConnectEquipment), SequenceNodeRole.Instruction),
            new("disconnect-all-equipment", typeof(DisconnectAllEquipment), SequenceNodeRole.Instruction),
            new("disconnect-equipment", typeof(DisconnectEquipment), SequenceNodeRole.Instruction),
            new("switch-profile", typeof(SwitchProfile), SequenceNodeRole.Instruction),
            new("loop-iterations", typeof(LoopCondition), SequenceNodeRole.Condition),
            new("loop-for-duration", typeof(TimeSpanCondition), SequenceNodeRole.Condition),
            new("loop-while-above-horizon", typeof(AboveHorizonCondition), SequenceNodeRole.Condition, NeedsAstrometryNatives: true),
            new("loop-while-altitude", typeof(AltitudeCondition), SequenceNodeRole.Condition, NeedsAstrometryNatives: true),
            new("loop-until-time", typeof(TimeCondition), SequenceNodeRole.Condition),
            new("meridian-flip", typeof(MeridianFlipTrigger), SequenceNodeRole.Trigger),
            new("dither-after-exposures", typeof(DitherAfterExposures), SequenceNodeRole.Trigger),
            new("autofocus-after-exposures", typeof(AutofocusAfterExposures), SequenceNodeRole.Trigger),
            new("unguided-exposure-guard", typeof(UnguidedExposureGuard), SequenceNodeRole.Trigger),
            new("center-after-drift", typeof(CenterAfterDriftTrigger), SequenceNodeRole.Trigger),
            new("autofocus-after-time", typeof(AutofocusAfterTimeTrigger), SequenceNodeRole.Trigger),
            new("autofocus-after-temperature-change", typeof(AutofocusAfterTemperatureChangeTrigger), SequenceNodeRole.Trigger),
            new("autofocus-after-hfr-increase", typeof(AutofocusAfterHFRIncreaseTrigger), SequenceNodeRole.Trigger),
            new("autofocus-after-filter-change", typeof(AutofocusAfterFilterChange), SequenceNodeRole.Trigger),
            new("reconnect", typeof(ReconnectTrigger), SequenceNodeRole.Trigger),
    };

    private static readonly Dictionary<string, SequenceKindEntry> ByKind = All.ToDictionary(e => e.Kind, StringComparer.Ordinal);

    /// <summary>The entry for a <c>kind</c>, or null when it is in no table (including <c>unknown</c> and <c>glover-exposure</c>).</summary>
    public static SequenceKindEntry? ForKind(string kind) => ByKind.GetValueOrDefault(kind);
}