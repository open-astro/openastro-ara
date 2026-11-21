# Sequence v2

Ara's own stored format for a sequence: a tree of containers, instructions, conditions and triggers, each node tagged with a stable `kind` string, under `"schemaVersion": "openastroara-sequence-v2"`. NINA's execution engine keeps running the sequence; only the stored format changes. NINA JSON becomes a one-way import.

Status: design spec for [#1623](https://github.com/open-astro/openastro-ara/issues/1623). The kind table (`SequenceKinds`), the field table, the validator and the generated [`sequence-v2.schema.json`](sequence-v2.schema.json) exist in `OpenAstroAra.Sequencer/Format/` (follow-up (a), part A); nothing calls the validator yet, and the stored format, the reader and writer, and the client are not built. It supersedes [ADR 0018](../adr/0018-a-sequence-is-stored-in-ninas-json-format.md) once follow-up (a) lands; this file does not edit that record. Follow-ups (a) to (d) are the last section.

## Decisions already made

These come from the issue and are not open here.

- The engine stays. Every node maps to one NINA engine class (the classes `HeadlessSequencerFactory.WithDefaults` registers), so the engine, its tests and its run behaviour do not change.
- NINA JSON is import only. Ara never writes it.
- A `kind` is an Ara name. A .NET `$type` class name never appears in a v2 document.
- Stored `openastroara-sequence-v1` bodies migrate mechanically to v2, and NINA import is lossless for the constructs the engine supports.
- The adaptive Glover exposure is an Ara-only `kind`.

## Vocabulary

- **Node**: one object in the tree. Has a `kind`.
- **Container**: a node that holds `items`, `conditions` and `triggers`.
- **Instruction**: a leaf node the engine executes (`capture`, `slew-radec`).
- **Condition**: a node in a container's `conditions` that decides whether the container loops.
- **Trigger**: a node in a container's `triggers` that fires between instructions and runs its `run` list.
- **Engine class**: the NINA-derived class under `OpenAstroAra.Sequencer` that a `kind` builds.
- **Placeholder**: the `unknown` node that stands in for something Ara cannot run.

## Document

```json
{
  "schemaVersion": "openastroara-sequence-v2",
  "name": "string",
  "description": "string, optional",
  "metadata": { "createdUtc": "ISO 8601, optional", "createdBy": "string, optional", "importedFrom": "object, optional" },
  "targets": [ { "id": "t1", "name": "string", "coordinates": { }, "positionAngle": 0 } ],
  "root": { "kind": "root" }
}
```

| Field | Type | Required | Notes |
|---|---|---|---|
| `schemaVersion` | string | yes | exactly `openastroara-sequence-v2` |
| `name` | string | yes | the sequence title, kept as found. `SequenceRootContainer.SequenceTitle` is not stored (it has no `[JsonProperty]`; v1 keeps the title as the root container's `Name`), so how the title is stored is part B's decision |
| `description` | string | no | free text, not read by the engine |
| `metadata` | object | no | not read by the engine. `importedFrom` is set by the client importer (see NINA JSON import) |
| `targets` | array | yes, may be empty | the targets the sequence points at; a `target` container names one by `targetId` |
| `root` | node, `kind` `root` | yes | exactly one |

Field names are the lowerCamelCase of the engine property they set (`ExposureTime` is `exposureTime`), `schemaVersion` included; a leading acronym is lowercased whole (`USBLimit` is `usbLimit`). The exceptions are listed once, in "Renamed fields" below, so migration stays mechanical. The daemon's REST wire format stays snake_case ([`design/API_CONTRACT.md`](../../design/API_CONTRACT.md)); a sequence body is a stored document, not a REST DTO, as it is under v1.

Unknown top-level or node fields are rejected by the validator (follow-up (a)), so a typo does not silently take a default. A field that is absent takes the engine class's constructor default (the value the factory prototype carries today), so a v1 body that omitted a field loads the same.

### Renamed fields

Fields whose v2 name is not the plain lowerCamelCase of the engine property:

| v2 field | Engine property | Kind |
|---|---|---|
| `expanded` | `IsExpanded` | every container |
| `run` | `TriggerRunner.Items` | every trigger |
| `provider` | `SelectedProvider` (the `$type` object becomes a name string) | `wait-for-time`, `loop-until-time` |
| `targetId` | the hoisted `Target` (`InputTarget`) | `target` |
| `binning` `{ "x", "y" }` | `Binning` (`BinningMode`) | `capture`, `glover-exposure` |
| `usbLimit` | `USBLimit` (acronym rule) | `set-usb-limit` |

The document `name` is the sequence title and the root node's own `name` is the root container's name; both are kept as found, and `sequenceTitle` is not a field.

Every other field in the tables below is the plain lowerCamelCase of its engine property, including `positionAngle` (`InputTarget.PositionAngle`, and `CenterAndRotate.PositionAngle`).

### Targets

An element of `targets`:

| Field | Type | Unit | Default | Engine property |
|---|---|---|---|---|
| `id` | string | | required, unique in the document | none (the link a `target` container uses) |
| `name` | string | | `""` | `InputTarget.TargetName` |
| `coordinates` | object | | required | `InputTarget.InputCoordinates`: `raHours` (int), `raMinutes` (int), `raSeconds` (double), `negativeDec` (bool), `decDegrees` (int), `decMinutes` (int), `decSeconds` (double); J2000 |
| `positionAngle` | number | degrees | `0` | `InputTarget.PositionAngle` (the legacy `Rotation` field is `360 - PositionAngle` and is not carried) |

Coordinates keep the engine's sexagesimal fields. The v1 body stores a target inside its `DeepSkyObjectContainer`; v2 hoists it so a client can list the targets without walking the tree.

### Common fields

| Field | On | Type | Default | Engine property |
|---|---|---|---|---|
| `kind` | every node | string | required | none (picks the engine class) |
| `name` | container, `unknown` | string | the engine class's default name | `SequenceContainer.Name` (the engine stores a name for containers only; an instruction, condition or trigger has none, so `name` on one is an unknown field) |
| `errorBehavior` | instruction, container | `continue-on-error`, `skip-instruction-set-on-error`, `abort-on-error`, `skip-to-sequence-end-instructions` | engine default | `SequenceItem.ErrorBehavior` (`InstructionErrorBehavior`; the fourth token is `SkipToSequenceEndInstructions`) |
| `attempts` | instruction, container | int | engine default | `SequenceItem.Attempts` |
| `items` | container | array of instruction or container nodes | `[]` | `SequenceContainer.Items` |
| `conditions` | container | array of condition nodes | `[]` | `SequenceContainer.Conditions` |
| `triggers` | container | array of trigger nodes | `[]` | `SequenceContainer.Triggers` |
| `expanded` | container | bool | engine default | `SequenceContainer.IsExpanded`, editor state, kept so v1 round-trips |

The container's execution strategy is not a field: `sequential`, `target`, `smart-exposure` and `root` build `SequentialStrategy`; `parallel` builds `ParallelStrategy`. The v1 `Strategy.$type` token goes away on migration.

A node's `Parent` is never stored; the tree gives it. The v1 `$id` / `$ref` reference bookkeeping (`PreserveReferencesHandling.All`) is not carried: v2 has no shared nodes, and the writer emits each node once.

## Kind to engine class

The namespace for every class is `OpenAstroAra.Sequencer.<group>` as listed; the assembly is `OpenAstroAra.Sequencer`. This is every class `HeadlessSequencerFactory.WithDefaults` registers at the head this spec was written against (47 instructions, 5 conditions, 5 containers, 10 triggers). `HeadlessSequencerFactory.Instantiate` skips `AboveHorizonCondition` and `AltitudeCondition` on a daemon without the astrometry natives; the kinds stay valid there, but a body naming them loads as a placeholder (today's behaviour, and the user sees the placeholder message below).

Units are the engine's. A unit that is not obvious is in the Fields column. Any field not listed for a kind is the engine's `[JsonProperty]` set for that class and follows the lowerCamelCase rule; `SequenceV2Fields` computes the exhaustive per-kind table from that contract, and tests pin it to these cells both ways.

### Containers

| `kind` | Engine class (`...Container`) | Fields (type, unit, default) |
|---|---|---|
| `root` | `SequenceRootContainer` | common fields only (the pause gate is runtime state and is not stored). Exactly one, only at `root` |
| `sequential` | `SequentialContainer` | common fields only |
| `parallel` | `ParallelContainer` | common fields only |
| `target` | `DeepSkyObjectContainer` | `targetId` (string, required, an `id` in `targets`); `exposureInfoList` (array of `ExposureInfo`: `count` (int), `filter` (string), `exposureTime` (double, seconds), `gain` (int), `offset` (int), `imageType` (string), `binningX` (short), `binningY` (short), `roi` (double); default `[]`); `exposureInfoListExpanded` (bool, editor state) |
| `smart-exposure` | `SmartExposure` (namespace `...SequenceItem.Imaging`) | common fields only; the engine builds its own children as NINA's Smart Exposure does, and they travel in `items` and `triggers` |

`StartAreaContainer`, `TargetAreaContainer` and `EndAreaContainer` are not registered in the factory, so a v1 body naming them loads as `UnknownSequenceContainer` today; they are `SequentialContainer`s, so v2 has no `kind` of their own and an import writes each as a `sequential` (see NINA JSON import). NINA's `ConditionalContainer` exists as a NINA type but not as a class here, and v2 has no `kind` for it (it becomes a placeholder).

### Instructions

| `kind` | Engine class | Namespace group | Fields (type, unit, default) |
|---|---|---|---|
| `annotation` | `Annotation` | `SequenceItem.Utility` | `text` (string) |
| `wait-for-user` | `WaitForUser` | `SequenceItem.Utility` | `text` (string); arms the root pause gate as a prompt |
| `wait-for-duration` | `WaitForTimeSpan` | `SequenceItem.Utility` | `time` (double, seconds, default `1`) |
| `wait-for-time` | `WaitForTime` | `SequenceItem.Utility` | `provider` (string, see Time sources, default `clock`), `hours` (int), `minutes` (int), `minutesOffset` (int), `seconds` (int) |
| `wait-for-sun-altitude` | `WaitForSunAltitude` | `SequenceItem.Utility` | `data` (`WaitLoopData` from `WaitForAltitudeBase`: `offset`, altitude offset in degrees, and `comparator`, an engine `ComparisonOperator` name: `EQUALS`, `LessThan`, `GreaterThan`, `NotEqual`) |
| `wait-until-safe` | `WaitUntilSafe` | `SequenceItem.SafetyMonitor` | none |
| `set-tracking` | `SetTracking` | `SequenceItem.Telescope` | `trackingMode` (`TrackingMode`: `Sidereal`, `Lunar`, `Solar`, `King`, `Custom`, `Stopped`, the engine names; default `Sidereal`) |
| `unpark-mount` | `UnparkScope` | `SequenceItem.Telescope` | none |
| `park-mount` | `ParkScope` | `SequenceItem.Telescope` | none |
| `find-home-mount` | `FindHome` | `SequenceItem.Telescope` | none |
| `slew-radec` | `SlewScopeToRaDec` | `SequenceItem.Telescope` | `inherited` (bool, take the enclosing target), `coordinates` (as in Targets), `center` (bool or null; null = the profile's "center after slew") |
| `slew-altaz` | `SlewScopeToAltAz` | `SequenceItem.Telescope` | `coordinates`: `azDegrees`, `azMinutes`, `azSeconds`, `altDegrees`, `altMinutes`, `altSeconds` |
| `tracking-probe` | `TrackingProbe` | `SequenceItem.Telescope` | `rungs` (array of double, seconds), `maxExposure` (double, seconds), `eccentricityTolerance` (double), `priorSeconds` (double or null), `ceilingSeconds` (double or null) |
| `move-focuser-absolute` | `MoveFocuserAbsolute` | `SequenceItem.Focuser` | `position` (int, steps) |
| `move-focuser-relative` | `MoveFocuserRelative` | `SequenceItem.Focuser` | `relativePosition` (int, steps) |
| `move-focuser-by-temperature` | `MoveFocuserByTemperature` | `SequenceItem.Focuser` | `slope` (double, steps per degree C, default `1`), `intercept` (double, steps), `absolute` (bool, default `true`) |
| `capture` | `TakeExposure` | `SequenceItem.Imaging` | `exposureTime` (double, seconds), `gain` (int, default `-1` = camera's current), `offset` (int, default `-1` = camera's current), `binning` (`{ "x": 1, "y": 1 }`, default 1x1), `imageType` (`LIGHT`, `DARK`, `FLAT`, `BIAS`, ..., default `LIGHT`), `exposureCount` (int, runtime counter, daemon-written), `calibrationTarget` (string, null or absent), `calibrationNight` (`yyyy-MM-dd`, null or absent) |
| `cool-camera` | `CoolCamera` | `SequenceItem.Camera` | `temperature` (double, degrees C), `duration` (double, minutes, default `0`), `waitForTemperature` (bool) |
| `warm-camera` | `WarmCamera` | `SequenceItem.Camera` | `duration` (double, minutes) |
| `set-usb-limit` | `SetUSBLimit` | `SequenceItem.Camera` | `usbLimit` (int); refuses at run time on Alpaca ([ADR 0023](../adr/0023-the-sequencer-owns-its-equipment-seam.md) rule 7) |
| `set-readout-mode` | `SetReadoutMode` | `SequenceItem.Camera` | `mode` (short) |
| `dew-heater` | `DewHeater` | `SequenceItem.Camera` | `onOff` (bool) |
| `start-guiding` | `StartGuiding` | `SequenceItem.Guider` | `forceCalibration` (bool), `calibrateEachNight` (bool) |
| `stop-guiding` | `StopGuiding` | `SequenceItem.Guider` | none |
| `dither` | `Dither` | `SequenceItem.Guider` | none (settings come from the profile) |
| `move-rotator-mechanical` | `MoveRotatorMechanical` | `SequenceItem.Rotator` | `mechanicalPosition` (float, degrees) |
| `set-switch-value` | `SetSwitchValue` | `SequenceItem.Switch` | `value` (double), `switchIndex` (short), `alpacaDeviceNumber` (int, default `-1`) |
| `open-dome-shutter` | `OpenDomeShutter` | `SequenceItem.Dome` | none |
| `close-dome-shutter` | `CloseDomeShutter` | `SequenceItem.Dome` | none |
| `park-dome` | `ParkDome` | `SequenceItem.Dome` | none |
| `find-home-dome` | `FindHomeDome` | `SequenceItem.Dome` | none |
| `slew-dome-azimuth` | `SlewDomeAzimuth` | `SequenceItem.Dome` | `azimuthDegrees` (double, degrees) |
| `enable-dome-sync` | `EnableDomeSynchronization` | `SequenceItem.Dome` | none |
| `disable-dome-sync` | `DisableDomeSynchronization` | `SequenceItem.Dome` | none |
| `sync-dome` | `SynchronizeDome` | `SequenceItem.Dome` | none |
| `switch-filter` | `SwitchFilter` | `SequenceItem.FilterWheel` | `filter` (`{ name, position, focusOffset, autoFocusExposureTime, autoFocusFilter }` or null) |
| `run-autofocus` | `RunAutofocus` | `SequenceItem.Autofocus` | none |
| `center-and-rotate` | `CenterAndRotate` | `SequenceItem.Platesolving` | `inherited` (bool), `coordinates` (as in Targets), `positionAngle` (double, degrees) |
| `flat-panel-flats` | `FlatPanelFlats` | `SequenceItem.FlatDevice` | `targetAdu` (double), `targetPercent` (double, default `50`), `targetAduTolerancePct` (double, default `5`), `frameCount` (int, default `30`), `brightness` (int), `minExposureSec` (double, default `0.01`), `maxExposureSec` (double, default `10`), `gain` (int, default `-1`), `offset` (int, default `-1`), `calibrationTarget`, `calibrationNight` |
| `sky-flats` | `SkyFlats` | `SequenceItem.FlatDevice` | `targetAdu` (double, default `25000`), `targetAduTolerancePct` (default `5`), `frameCount` (default `20`), `minExposureSec` (default `0.01`), `maxExposureSec` (default `10`), `stopAtMaxAdu` (default `50000`), `stopAtMinAdu` (default `5000`), `gain` (default `-1`), `offset` (default `-1`), `calibrationTarget`, `calibrationNight` |
| `open-flat-panel-cover` | `OpenFlatPanelCover` | `SequenceItem.FlatDevice` | none |
| `close-flat-panel-cover` | `CloseFlatPanelCover` | `SequenceItem.FlatDevice` | none |
| `connect-all-equipment` | `ConnectAllEquipment` | `SequenceItem.Connect` | none |
| `connect-equipment` | `ConnectEquipment` | `SequenceItem.Connect` | `selectedDevice` (string, default `""`; the role key the connections port takes, [ADR 0023](../adr/0023-the-sequencer-owns-its-equipment-seam.md) rule 3) |
| `disconnect-all-equipment` | `DisconnectAllEquipment` | `SequenceItem.Connect` | none |
| `disconnect-equipment` | `DisconnectEquipment` | `SequenceItem.Connect` | `selectedDevice` (string, default `""`) |
| `switch-profile` | `SwitchProfile` | `SequenceItem.Connect` | `selectedProfileId` (GUID string), `reconnect` (bool) |

### Conditions

| `kind` | Engine class | Namespace | Fields (type, unit, default) |
|---|---|---|---|
| `loop-iterations` | `LoopCondition` | `Conditions` | `iterations` (int), `completedIterations` (int, runtime counter, daemon-written) |
| `loop-for-duration` | `TimeSpanCondition` | `Conditions` | `hours` (int), `minutes` (int), `seconds` (int) |
| `loop-until-time` | `TimeCondition` | `Conditions` | `provider` (string, see Time sources), `hours` (int), `minutes` (int), `minutesOffset` (int), `seconds` (int) |
| `loop-while-above-horizon` | `AboveHorizonCondition` | `Conditions` | `hasDsoParent` (bool), `data` (`WaitLoopData` from `LoopForAltitudeBase`: `offset` in degrees, `comparator` as for `wait-for-sun-altitude`) |
| `loop-while-altitude` | `AltitudeCondition` | `Conditions` | `hasDsoParent` (bool), `data` (`WaitLoopData` from `LoopForAltitudeBase`: altitude in degrees as `offset`, `comparator` as for `wait-for-sun-altitude`) |

### Triggers

A trigger's `run` is a list of instruction nodes. It maps to the trigger's `TriggerRunner` (a `SequentialContainer`); migration reads `TriggerRunner.Items` into `run`.

| `kind` | Engine class | Namespace | Fields (type, unit, default) |
|---|---|---|---|
| `meridian-flip` | `MeridianFlipTrigger` | `Trigger.MeridianFlip` | `recalibrateGuider` (bool) |
| `dither-after-exposures` | `DitherAfterExposures` | `Trigger.Guider` | `afterExposures` (int, default `1`); `run` holds a `dither` |
| `autofocus-after-exposures` | `AutofocusAfterExposures` | `Trigger.Autofocus` | `afterExposures` (int, default `1`); `run` holds a `run-autofocus` |
| `autofocus-after-time` | `AutofocusAfterTimeTrigger` | `Trigger.Autofocus` | `amount` (double, minutes, default `30`) |
| `autofocus-after-temperature-change` | `AutofocusAfterTemperatureChangeTrigger` | `Trigger.Autofocus` | `amount` (double, degrees C, default `5`) |
| `autofocus-after-hfr-increase` | `AutofocusAfterHFRIncreaseTrigger` | `Trigger.Autofocus` | `amount` (double, percent), `sampleSize` (int, default `3`) |
| `autofocus-after-filter-change` | `AutofocusAfterFilterChange` | `Trigger.Autofocus` | none |
| `unguided-exposure-guard` | `UnguidedExposureGuard` | `Trigger.Telescope` | `rungs` (array of double, seconds), `maxExposure` (double, seconds), `eccentricityTolerance` (double), `goodEccentricity` (double), `stepDownAfter` (int), `stepUpAfter` (int) |
| `center-after-drift` | `CenterAfterDriftTrigger` | `Trigger.Platesolving` | `coordinates` (as in Targets), `distanceArcMinutes` (double, arcminutes), `afterExposures` (int) |
| `reconnect` | `ReconnectTrigger` | `Trigger.Connect` | `selectedDevice` (string); `run` holds a `connect-equipment` |

### Time sources

`provider` on `wait-for-time` and `loop-until-time` is a string that names one `IDateTimeProvider` (namespace `OpenAstroAra.Sequencer.Utility.DateTimeProvider`). It replaces the v1 `SelectedProvider` object with its `$type`. Default `clock`.

| `provider` | Class |
|---|---|
| `clock` | `TimeProvider` (the observing-night clock) |
| `sunset` | `SunsetProvider` |
| `sunrise` | `SunriseProvider` |
| `civil-dusk` | `CivilDuskProvider` |
| `civil-dawn` | `CivilDawnProvider` |
| `nautical-dusk` | `NauticalDuskProvider` |
| `nautical-dawn` | `NauticalDawnProvider` |
| `dusk` | `DuskProvider` |
| `dawn` | `DawnProvider` |
| `meridian` | `MeridianProvider` |

### Placeholder

`kind: "unknown"` is the only node with no engine class of its own. It is built from `UnknownSequenceContainer`, `UnknownSequenceItem`, `UnknownSequenceCondition` or `UnknownSequenceTrigger`, picked by `role`.

| Field | Type | Meaning |
|---|---|---|
| `role` | `container`, `instruction`, `condition`, `trigger` | where it sits; picks the class |
| `name` | string | set to `originalType` by the importer and the migration; the engine builds the displayed name from it (`UnknownSequenceItem.Name` wraps `base.Name`) |
| `originalType` | string | the NINA `$type` token as found, or the v1 token |
| `originalPayload` | object | the source node, kept so nothing is lost and a later Ara release can retry it |
| `reason` | string | why it was not mapped |

A placeholder is written only by the client importer and the migration. The importer is client code, so the validator accepts an `unknown` node only with `role`, `originalType`, `originalPayload` and `reason` (`role` picks the engine class, so it is required), and the editor never offers one to add. The daemon keeps it in the library as it keeps it today. The engine behaviour is today's: an `unknown` instruction throws `SequenceItemSkippedException` ("Unknown instruction will be skipped - &lt;name&gt;"), and the placeholder's validation never passes (`Validate()` returns `false`). A failed condition check on an `unknown` condition ends its container's loop, which is how an unregistered altitude condition skipped a whole target block before `AboveHorizonCondition` was registered (`HeadlessSequencerFactory.Instantiate`).

### Ara-only kind: `glover-exposure`

See the adaptive section below. It has no engine class today; follow-up (d) adds exactly one.

## NINA JSON import

Import is client-side and one-way. The NINA file lives on the client machine, so the client reads it, maps it and uploads the result; the daemon has no import endpoint and never reads a NINA `$type` for this purpose. This is the recorded 2026-07-15 decision that removed `POST /sequences/import` (see the removal note in `OpenAstroAra.Server/Endpoints/SequenceEndpoints.cs`).

**Before.** [`nina_import.dart`](../../client/openastroara_client/lib/util/nina_import.dart) read the NINA `.json`, rewrote only the `$type` strings (NINA namespace to `OpenAstroAra` namespace), collected a `warnings` list for sequencer types the client's editor catalog does not know, backfilled `schemaVersion` `openastroara-sequence-v1`, and the result was uploaded through the ordinary idempotent create (or saved as an offline draft). The daemon's schema validator gates that create. The daemon still loads NINA-flavoured bodies at run time through `JsonCreationConverter` (v1 compatibility; follow-up (c) ends the need).

**Target.** The importer stays in the client, as the successor of `nina_import.dart`, and writes v2:

1. **Detect.** A body with a top-level `$type` and no `schemaVersion` is NINA JSON. A body whose `schemaVersion` is `openastroara-sequence-v2` is not mapped and is uploaded as written. A body with any other `schemaVersion` (`openastroara-sequence-v1` included) is not a NINA file and is refused. Anything else is refused on the client with "This file is not a NINA sequence.", the one sentence for every refusal but a shared node. Malformed JSON, a body that is not an object, or a root that is not a container refuses the whole import; nothing is uploaded.
2. **Map `$type` to `kind`.** The client takes the class name of each `$type` when its namespace is `NINA.Sequencer.` or `OpenAstroAra.Sequencer.` (any assembly form) and looks it up in this spec's kind tables; a class in another namespace never maps, even when its name is a kind's class name. The five `NINA.Plugins.Connector` instructions (`ConnectAllEquipment`, `ConnectEquipment`, `DisconnectEquipment`, `DisconnectAllEquipment`, `SwitchProfile`) map to the same-named Ara classes, as `SequenceItemCreationConverter.PluginMergeMigration` does on the daemon today, and the Connector `ReconnectTrigger` maps to `reconnect`, as `SequenceTriggerCreationConverter.PluginMergeMigration` does. That converter also maps the Connector `ReconnectOnDownloadFailure`, but Ara has no such class (`OpenAstroAra.Sequencer/Trigger/Connect/` holds only `ReconnectTrigger`), so it loads as `UnknownSequenceTrigger` today and the importer writes a placeholder for it. No `NinaTypeRemapper.ResolveType` step runs on the server.
3. **Map fields.** Each property becomes its v2 field (see "Renamed fields"); a property that is not a field of the node's `kind` is dropped. `ImageType` `DARKFLAT` becomes `DARK`. A NINA `Strategy.$type` is dropped (the `kind` decides it). A `SelectedProvider.$type` becomes a `provider` string; a provider the table does not name is left out, so the field takes its default. A `DeepSkyObjectContainer`'s `Target` is hoisted into `targets` and the container gets a `targetId`. A `$ref` to a node already seen is refused as "shared node" (a node position is a list entry, a trigger's `TriggerRunner` or the root); a `$ref` in a value position (a `Binning` or `Filter` two instructions share) is the earlier object with that `$id`, expanded. A registered container other than `SequenceRootContainer` at the top is wrapped in a `root`.
   - **Area containers.** `StartAreaContainer`, `TargetAreaContainer` and `EndAreaContainer` each become a `sequential` container with the same `Name`; their children, conditions and triggers are imported. (A real NINA export is a root holding the three area containers, with the target blocks inside `TargetAreaContainer`.)
   - **Enum words.** NINA writes an enum as a number; the v2 field holds a word. `errorBehavior` is `continue-on-error`, `skip-instruction-set-on-error`, `abort-on-error` or `skip-to-sequence-end-instructions` for 0 to 3. `trackingMode` is the engine name, `Sidereal`, `Lunar`, `Solar`, `King`, `Custom` or `Stopped` for 0 to 5. `data.comparator` is the engine `ComparisonOperator` name (`EQUALS`, `LessThan`, `LessThanOrEqual`, `GreaterThan`, `GreaterThanOrEqual`, `NotEqual` for 0 to 5), with `GreaterThanOrEqual` becoming `GreaterThan` and `LessThanOrEqual` becoming `LessThan`. A string stays as found.
   - **Keys.** Every key is lowerCamelCase at every depth (a leading acronym lowercased whole: `RAHours` is `raHours`, `ROI` is `roi`), inside `exposureInfoList` elements, `coordinates` and `data` too, except inside a placeholder's `originalPayload`, which is the source node as found. `filter` keeps only `name`, `position`, `focusOffset`, `autoFocusExposureTime` and `autoFocusFilter`, from NINA's `_name`, `_position`, ... (the underscore is removed); `binning` keeps `x` and `y`. A key of a nested value that is not listed is dropped. `$type`, `$id` and `Parent` never reach the output.
   - **Old altitude fields.** `Altitude`, `AltitudeOffset`, `UserMoonAltitude`, `UserSunAltitude` (all `data.offset`), `Comparator` (`data.comparator`) and `Coordinates` (`data.coordinates`) on `wait-for-sun-altitude`, `loop-while-above-horizon` and `loop-while-altitude` fold into `data`. When a file has both, the old field wins over `Data`, as in the engine.
4. **Supported constructs import losslessly**: every NINA class the tables list, with every field the class carries. There is no export back to NINA, so lossless means "nothing the engine runs is dropped or changed".
5. **Not supported: placeholder or refusal.**

| NINA construct | What import does |
|---|---|
| a `$type` with no `kind` in the tables (a NINA instruction Ara does not ship, any third-party plugin type, MGEN, `ConditionalContainer`, `IfContainer`-style containers) | the node becomes a placeholder (`kind: "unknown"`) with `originalType`, `originalPayload` and `reason`; its children are inside `originalPayload` and are not imported; the import goes on |
| a plugin type under `NINA.Plugins.*` other than the five Connector instructions and the Connector `ReconnectTrigger` (the Connector `ReconnectOnDownloadFailure` included) | the same placeholder |
| a node with no `$type` | the same placeholder, `originalType` empty |
| a device id (`SelectedDevice`, `AlpacaDeviceNumber`) that names a NINA ASCOM device: a non-empty `selectedDevice` that is not a rig role key (`Camera`, `Filter Wheel`, `Focuser`, `Rotator`, `Telescope`, `Mount`, `Guider`, `Switch`, `Flat Panel`, `Weather`, `Dome`, `Safety Monitor`), or an `alpacaDeviceNumber` other than `-1` | kept as found; the client adds a warning `equipment_unmatched` (the user reassigns it in the editor) |
| malformed JSON, a body that is not an object, or a root that is not a container | the whole import is refused; nothing is uploaded |

6. **What the user is told.** The client collects `warnings`, one per placeholder and per unmatched device, and shows them before upload. The text of a placeholder warning is exactly:

   `"<originalType>" is not supported by OpenAstro Ara and was not imported. It stays in the sequence as a placeholder that is skipped when the sequence runs.`

   The text of an `equipment_unmatched` warning is `The device "<id>" of "<kind>" is not one of this rig's equipment roles. It was kept as found; assign it in the editor.` (`<id>` is the device number for an `alpacaDeviceNumber`).

   The placeholder node itself shows, in the editor and in the sequence's validation list, today's engine text unchanged: the name `<Unknown Instruction - <name>> ` (the node's `name`, which the importer sets to `originalType`) and the issue "Unknown instruction detected! Please double check your sequence and inspect which instructions became invalid. Furthermore validate that everything is up to date." (`LblUnknownInstruction`, `LblUnknownInstructionValidation`). When the engine reaches it, the run log says "Unknown instruction will be skipped - <name>". The client banner counts the warnings ("3 issues need attention"). An import with at least one placeholder still saves; it cannot start until the user removes or replaces each placeholder, because a sequence with a placeholder fails its validation (`Validate()` is `false`).
7. **Provenance.** The client sets `metadata.importedFrom` to `{ "format": "nina", "fileName": "<name>", "importedUtc": "<ISO 8601>" }` in the v2 body (`fileName` is the picked file's name with its extension, `importedUtc` is UTC). The daemon keeps no source file and no `sequences/imported/` directory.
8. **Daemon gate.** The daemon's v2 validator (follow-up (a)) gates the create and answers HTTP 422 on an invalid body. A placeholder from the client passes the create only when it carries `role`, `originalType`, `originalPayload` and `reason`.
9. **No Glover.** NINA has no equivalent of `glover-exposure`, so import never creates one; a NINA `TakeExposure` becomes a `capture`.

[`design/PORT_PLAYBOOK.md`](../../design/PORT_PLAYBOOK.md) section 38.4 still describes the old server import flow and is out of date; this PR does not edit the playbook.

## Migration of stored v1 bodies

A v1 body is a NINA-shaped `$type` tree with `"schemaVersion": "openastroara-sequence-v1"`. Migration turns it into a v2 document with the same `$type` to `kind` and field mapping as import (steps 2 and 3 above), run on the daemon over the body already stored, with no change to its meaning.

1. **When.** Once, at daemon startup, after the profile directory is open and before the sequence service accepts a request, for every `sequences/library/*.json`, then for the checkpoint. It is not run lazily on read, so a run never meets a v1 body. A body uploaded as v1 after the upgrade is rejected by the validator (follow-up (a) decides whether a v1 upload is migrated on the way in or refused; this spec says refused, 422, "This daemon stores sequence v2. Upload a v2 document.").
2. **Library bodies.** The `Body` of each library file is migrated and the file is rewritten atomically (temp file and rename, as `ActiveSequenceCheckpoint` writes). Before the first rewrite of a file, the v1 file is copied to `sequences/library/.v1-backup/<id>.json`, once; a later run never overwrites a backup. Block and target names are kept exactly, because the client and the rig find a Simple Mode target block by its name, and the end-of-night `Flats` and `Calibration` blocks by theirs ([ADR 0010](../adr/0010-simple-mode-queue-is-one-sequence-on-the-rig.md) rules 2 and 4).
3. **Active checkpoint.** `sequences/active/current.json` holds a `SequenceRunStateDto` (the `SequenceId` of the library sequence and run counters), not a body, so it needs no body rewrite. Migration still reads it: the run it names resumes only from the migrated library body, and the reconciler's rule is unchanged (a checkpoint found at startup is never auto-resumed; [`design/PORT_PLAYBOOK.md`](../../design/PORT_PLAYBOOK.md) §28.2). If a checkpoint ever carries a body, the same function migrates it in place. A checkpoint whose sequence failed migration is quarantined to `current.json.corrupt.<unix-ts>`, the way a damaged checkpoint is today.
4. **Mechanical mapping.** Each `$type` goes to its `kind` through the tables; each property goes to its v2 field (see "Renamed fields"); `Strategy` is dropped; `TriggerRunner.Items` becomes `run`; `SelectedProvider` becomes `provider`; each `DeepSkyObjectContainer.Target` is hoisted to `targets` with a fresh id (`t1`, `t2`, in tree order) and the container gets `targetId`; `$id` / `$ref` are expanded; the v1 `schemaVersion` is replaced. Runtime counters (`exposureCount`, `completedIterations`) are kept. A node whose `$type` the migration cannot map becomes a placeholder as in import (the user keeps what they had; the daemon logs it).
5. **Idempotent.** A body that already says `openastroara-sequence-v2` is left alone, byte for byte: the migration checks `schemaVersion` first and writes nothing. Running it twice on a v1 body gives the first run's result; a crash between the backup copy and the atomic rewrite leaves the v1 file in place, so the next startup redoes it.
6. **A body that fails.** Malformed JSON, a non-object body, or a root that is not a container: the file is not rewritten and is not deleted. The daemon logs the file name and the error (the file name escaped, CWE-117), marks the sequence unreadable in the list (the sequence service returns it with a `migration_failed` problem, RFC 7807, on GET), and posts one notification: "Sequence '<name>' could not be updated to the new format. It was left unchanged." The other files still migrate. A daemon never refuses to start because a sequence failed. A sequence that failed cannot start until the user re-uploads it.
7. **Downgrade.** There is none. A v2 library is not read by a v1 daemon; the `.v1-backup` copies are the way back.

## Adaptive exposure (Glover)

The adaptive runtime exposure measures the sky background from the live frames and sets the sub length during the run. Today's Glover model lives in planning ([`SensorQeLibrary`](../../OpenAstroAra.Server/Services/SensorQeLibrary.cs) feeds the client's optimal-sub calculator); this node brings it into the run.

- **`kind`**: `glover-exposure` (instruction). **No NINA equivalent**, so no engine class exists for it. Follow-up (d) adds one new class under `OpenAstroAra.Sequencer.SequenceItem.Imaging` and registers it in `HeadlessSequencerFactory`. Everything else in this spec stays 1:1 with a NINA class; this is the one exception.
- **Behaviour**: a `capture` that, instead of a fixed `exposureTime`, measures the sky background (median ADU of the last `measureFrames` light frames, converted with the profile's gain and electrons-per-ADU) and sets the next sub to the Glover optimum, clamped to `[minSubSeconds, maxSubSeconds]`. It reads the frames through the run's `IFrameQualityFeed`, the seam `UnguidedExposureGuard` uses. With fewer than `measureFrames` frames so far it exposes `startSubSeconds`.
- **Fields** (proposed, follow-up (d) fixes them): `startSubSeconds` (double, seconds, required), `minSubSeconds` (double, seconds), `maxSubSeconds` (double, seconds), `measureFrames` (int, default `3`), `swampFactor` (double, how far sky noise must exceed read noise), `frameCount` (int), plus the `capture` fields `gain`, `offset`, `binning` and `imageType` (`LIGHT` only).
- **Import**: never produces it (see NINA JSON import, step 9).
- **Migration**: never produces it; v1 has no such node.
- **A v2 document that has one**: valid on a daemon that has follow-up (d). A daemon without it rejects the body as an unknown `kind` (422), never as a placeholder, so a sequence cannot silently lose its adaptive sub length.
- **No NINA export exists**, so there is no export rule for it.

## Example

A real target (M42), a filter loop, an autofocus trigger, a loop-until-time condition and a Glover node. The site comes from the profile, not the document, so no coordinates beyond the target's appear here.

```json
{
  "schemaVersion": "openastroara-sequence-v2",
  "name": "M42 LRGB with adaptive Ha",
  "description": "Orion Nebula, evening session",
  "metadata": { "createdUtc": "2026-11-17T08:00:00Z", "createdBy": "example" },
  "targets": [
    {
      "id": "t1",
      "name": "M42",
      "coordinates": { "raHours": 5, "raMinutes": 35, "raSeconds": 17.3, "negativeDec": true, "decDegrees": 5, "decMinutes": 23, "decSeconds": 28 },
      "positionAngle": 0
    }
  ],
  "root": {
    "kind": "root",
    "name": "M42 LRGB with adaptive Ha",
    "items": [
      { "kind": "connect-all-equipment" },
      { "kind": "unpark-mount" },
      {
        "kind": "target",
        "name": "M42",
        "targetId": "t1",
        "items": [
          { "kind": "slew-radec", "inherited": true, "center": true },
          { "kind": "run-autofocus" },
          { "kind": "start-guiding", "forceCalibration": false, "calibrateEachNight": false },
          {
            "kind": "sequential",
            "name": "Filter loop",
            "conditions": [
              { "kind": "loop-until-time", "provider": "nautical-dawn", "hours": 0, "minutes": 0, "minutesOffset": -30, "seconds": 0 }
            ],
            "triggers": [
              { "kind": "autofocus-after-filter-change", "run": [ { "kind": "run-autofocus" } ] },
              { "kind": "dither-after-exposures", "afterExposures": 3, "run": [ { "kind": "dither" } ] }
            ],
            "items": [
              { "kind": "switch-filter", "filter": { "name": "L", "position": 0 } },
              { "kind": "capture", "exposureTime": 120, "gain": 100, "offset": 50, "binning": { "x": 1, "y": 1 }, "imageType": "LIGHT" },
              { "kind": "switch-filter", "filter": { "name": "Ha", "position": 4 } },
              { "kind": "glover-exposure", "startSubSeconds": 180, "minSubSeconds": 60, "maxSubSeconds": 600, "measureFrames": 3, "frameCount": 10, "gain": 100, "offset": 50, "binning": { "x": 1, "y": 1 }, "imageType": "LIGHT" }
            ]
          }
        ]
      },
      { "kind": "stop-guiding" },
      { "kind": "park-mount" }
    ]
  }
}
```

The `loop-until-time` condition is the container's loop test: the filter loop repeats until 30 minutes before nautical dawn. The example names no device host. A document that names one uses `192.0.2.10`.

## Validation

`SequenceV2Validator.Validate` (part A of follow-up (a)) holds these rules, beyond today's `SequenceSchemaValidator` ones (object body, schema version, at least one capturable instruction reachable from root). It returns every error it finds, in walk order (document members, `targets`, then `root` depth first: node fields, `conditions`, `triggers` with each `run`, `items`), at most 100:

- every `kind` is in the tables above, or the daemon has the Glover class for `glover-exposure` (`unknown_kind`);
- the document has `schemaVersion` (`openastroara-sequence-v2`), `name`, `targets` and `root` (`missing_field`, `schema_version`);
- `root` is the document member `root` and its `kind` is `root` (`root_kind`); a `root` anywhere else is `misplaced_root`; a `target`'s `targetId` is an `id` in `targets` (`unknown_target`), target ids are unique (`duplicate_target_id`), and a target element has `id` and `coordinates`;
- a node sits in a list that takes its role: `items` take containers and instructions, `conditions` conditions, `triggers` triggers, a trigger's `run` instructions (`wrong_role`; a placeholder counts by its `role`);
- a `capture`, `flat-panel-flats` or `sky-flats` node or a `glover-exposure` counts as capturable, only when it is reachable from `root` through `items`: one in a trigger's `run`, in a condition or inside a placeholder's payload does not count (`no_capturable_instruction`); a template body may waive the rule;
- no unknown fields, on the document, on a target element and on each node (`unknown_field`; a typo is a 422 naming the node path);
- a known field has the JSON value kind of the engine property's type: a string for a string, an enum or a GUID, an integer token that fits the property's integer type, a number, a boolean, an array for a collection, an object for another class (`wrong_type`). `null` is valid only where the engine declares the property nullable (`center`, `filter`, `priorSeconds`, `ceilingSeconds`, `calibrationTarget`, `calibrationNight`). `kind`, `targetId` and `provider` are strings and never null. Enum vocabulary, GUID format and the keys inside nested objects (`coordinates`, `binning`, `filter`, `data`) are not checked;
- an `unknown` node carries `role`, `originalType`, `originalPayload` and `reason` (the client importer and the migration write it; the editor never offers one); its payload is opaque and is not walked.

An error is `{ code, path, message }` with a path such as `$.root.items[2].triggers[0].run[1]`; a member of the wrong JSON kind is one `wrong_type` at the member's path and never an exception. The codes are `body_not_object`, `schema_version`, `missing_field`, `wrong_type`, `unknown_field`, `unknown_kind`, `wrong_role`, `root_kind`, `misplaced_root`, `duplicate_target_id`, `unknown_target` and `no_capturable_instruction`. A client name quoted in a message is cut to 64 characters. The validator logs nothing; a caller that logs a path or message escapes it (CWE-117).

[`sequence-v2.schema.json`](sequence-v2.schema.json) is the machine-readable form of the field table (JSON Schema 2020-12, one `$defs` object per kind, `additionalProperties: false`). It is generated by `SequenceV2Schema.Generate()` and a test fails when the checked-in file differs. It states names and value kinds only; the rules that need the whole document (roles by list, `targetId` resolution, capturable) are the validator's.

## Follow-up issues

Four issues, filed after the maintainer signs this spec off. Each is a separate PR against `master`.

### (a) Schema and validator

**Summary.** Give v2 a machine-readable schema and a server validator, and make v2 the format the daemon stores. This supersedes [ADR 0018](../adr/0018-a-sequence-is-stored-in-ninas-json-format.md): its rules 1 to 3 (NINA's schema with `$type` on every node, `TypeNameHandling` never set on read, schema version `v1`) change. The PR changes `docs/adr/`, so it needs a collaborator's `ADR authorized <sha>` comment (`adr-authorization` status, `docs/adr/README.md` "Changing a record"); the record's "How to reverse" section already lists this move.

**Desired outcome.** A sequence is written and read as a v2 document by one writer. `SequenceSchemaValidator.SchemaVersion` is `openastroara-sequence-v2` and the validator holds the Validation rules above. A `kind` to engine class table is declared once in the Sequencer and the factory builds from it. The client's sequence models and the run builders ([ADR 0010](../adr/0010-simple-mode-queue-is-one-sequence-on-the-rig.md)'s `imaging_run_body.dart`) emit v2. The daemon no longer reads a `$type` token outside the migration and the run-time `JsonCreationConverter` compatibility path.

**Acceptance criteria.**
- ADR 0018 is superseded by a new record that states the v2 rules; the PR names it under `## Decision records` and carries `ADR authorized <sha>`.
- A document in this spec's Example validates; each rule in Validation has a test that fails when the rule is removed.
- `kind` to class coverage test: every class `WithDefaults` registers has exactly one `kind`, and every `kind` builds that class.
- A v2 body round-trips through the engine tree and back byte-identically (field order fixed).
- The client's generated bodies (Simple Mode blocks, `Flats`, `Calibration`) are v2 and keep their block names.
- `SequenceSchemaValidatorTest`, the round-trip tests and the Simple Mode plan tests are updated, not deleted.

**Out of scope.** The NINA importer (b), the migration (c), the Glover node (d), Native AOT publishing, any engine change.

### (b) NINA importer

**Summary.** Today the client's `nina_import.dart` rewrites NINA `$type` strings to the OpenAstroAra namespace, backfills `schemaVersion` `openastroara-sequence-v1` and uploads through the ordinary idempotent create (or an offline draft); the daemon removed `POST /sequences/import` (2026-07-15). Move that path to v2 in the client, as in "NINA JSON import".

**Desired outcome.** The client maps a NINA `.json` to a v2 document through the spec's kind table, shows `warnings` for placeholder and unmatched-device nodes before upload, records `metadata.importedFrom`, and uploads through the ordinary create. The daemon's v2 validator answers 422 on an invalid body. No import endpoint, no server-side `NinaTypeRemapper.ResolveType` step, no source file kept on the daemon.

**Acceptance criteria.**
- A fixture set of real NINA files (a target block, a Smart Exposure with triggers, the Connector plugin instructions, the Connector `ReconnectTrigger` and `ReconnectOnDownloadFailure` triggers, a plugin instruction Ara does not ship) imports with the node counts and field values asserted, in Dart tests.
- The warning text for a placeholder equals the sentence in this spec, and the placeholder shows the `LblUnknownInstruction` and `LblUnknownInstructionValidation` text.
- `DARKFLAT` becomes `DARK`; the five Connector instructions and the Connector `ReconnectTrigger` (to `reconnect`) map; `ReconnectOnDownloadFailure` and `ConditionalContainer` become placeholders; the three area containers become `sequential` and a real NINA file (a root holding them) imports with a capture reachable through `items`.
- Malformed JSON, a non-object body and a non-container root are refused and upload nothing.
- The client import tests (`test/util/nina_import_test.dart`, `test/widgets/sequencer/sequence_import_test.dart`) are ported to v2, not deleted. The daemon's `NinaImportFidelityTest` and `JsonCreationConverterRemapTest` pin the run-time `$type` path (`NinaTypeRemapper`), which v1 compatibility and the migration (c) still use; this issue does not touch them.

**Out of scope.** NINA export, importing plugin instructions, equipment matching beyond the `equipment_unmatched` warning, a daemon import endpoint, editing `design/PORT_PLAYBOOK.md`.

### (c) Migration of stored v1 bodies

**Summary.** Migrate every stored `openastroara-sequence-v1` body to v2 at daemon startup, as in "Migration of stored v1 bodies".

**Desired outcome.** After the first start on the new daemon, every library sequence is v2, the v1 copy is kept in `.v1-backup`, and a body that fails is reported without stopping the daemon.

**Acceptance criteria.**
- A v1 library of the daemon's own generated bodies (Simple Mode queue with `Flats` and `Calibration`, a sky-flat sequence, a calibration run, the bundled starter templates) migrates, and block names are unchanged.
- Running the migration twice leaves the files byte-identical after the first run.
- A crash between backup and rewrite (simulated) leaves the v1 file, and the next start migrates it.
- A malformed body is left untouched, the sequence is listed with a `migration_failed` problem, a notification is posted, and other files migrate.
- A checkpoint is read, never auto-resumed, and quarantined when its sequence failed.
- Logged file names are escaped.

**Out of scope.** Downgrade, migrating bodies outside `sequences/library`, the client importer (b), schema changes (a).

### (d) Adaptive Glover exposure

**Summary.** Add the `glover-exposure` instruction: measure sky background from live frames and set the sub length during the run.

**Desired outcome.** A sequence can ask for sub lengths the sky sets, not a fixed number, using the same Glover model the planner uses. It is the one Ara-only engine class.

**Acceptance criteria.**
- One new engine class under `OpenAstroAra.Sequencer.SequenceItem.Imaging`, registered in `HeadlessSequencerFactory`, with the `kind` and fields fixed in a spec amendment.
- Sub length for a given measured background, gain, read noise and QE equals the planner's value for the same inputs (a shared test vector).
- Clamped to `[minSubSeconds, maxSubSeconds]`; `startSubSeconds` used until `measureFrames` frames exist.
- A daemon without the class rejects a body that names the kind (422), never as a placeholder.
- A fake frame feed drives the tests; no rig is needed.

**Out of scope.** Changing the planner, a NINA equivalent or export, new camera calibration data, per-filter adaptive profiles.

## Known limits

- The per-kind field table is computed from the engine's `[JsonProperty]` set and pinned to the cells above in both directions; a field an engine class gains changes the table, the schema and a test, and is added to this spec in the same PR.
- The setter-only NINA deprecation shims on the altitude classes (`altitude`, `altitudeOffset`, `comparator`, `coordinates`, `userMoonAltitude`, `userSunAltitude` on `wait-for-sun-altitude`, `loop-while-above-horizon`, `loop-while-altitude`) are read by v1 and NINA bodies but never written, so they are not v2 fields; the importer and the migration fold them into `data`, and the old field wins over `Data`.
- A document with a `$ref` to a shared node is refused by the client importer; no NINA type Ara supports produces one. A `$ref` to a value is expanded.
- No NINA export, by decision.
- Migration cannot restore a v1 body's `$id` numbering; the v1 backup is the only way back to it.
