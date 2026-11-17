# Sequence v2

Ara's own stored format for a sequence: a tree of containers, instructions, conditions and triggers, each node tagged with a stable `kind` string, under `"schemaVersion": "openastroara-sequence-v2"`. NINA's execution engine keeps running the sequence; only the stored format changes. NINA JSON becomes a one-way import.

Status: design spec for [#1623](https://github.com/open-astro/openastro-ara/issues/1623). Nothing here is built. It supersedes [ADR 0018](../adr/0018-a-sequence-is-stored-in-ninas-json-format.md) once follow-up (a) lands; this file does not edit that record. Follow-ups (a) to (d) are the last section.

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
  "targets": [ { "id": "t1", "name": "string", "coordinates": { }, "positionAngleDegrees": 0 } ],
  "root": { "kind": "root" }
}
```

| Field | Type | Required | Notes |
|---|---|---|---|
| `schemaVersion` | string | yes | exactly `openastroara-sequence-v2` |
| `name` | string | yes | the sequence title; maps to `SequenceRootContainer`'s `SequenceTitle` |
| `description` | string | no | free text, not read by the engine |
| `metadata` | object | no | not read by the engine. `importedFrom` is set by the importer (see NINA import) |
| `targets` | array | yes, may be empty | the targets the sequence points at; a `target` container names one by `targetId` |
| `root` | node, `kind` `root` | yes | exactly one |

Field names are lowerCamelCase of the engine property they set (`ExposureTime` is `exposureTime`), `schemaVersion` included. This one rule is what makes migration mechanical. The daemon's REST wire format stays snake_case ([`design/API_CONTRACT.md`](../../design/API_CONTRACT.md)); a sequence body is a stored document, not a REST DTO, as it is under v1.

Unknown top-level or node fields are rejected by the validator (follow-up (a)), so a typo does not silently take a default. A field that is absent takes the engine class's constructor default (the value the factory prototype carries today), so a v1 body that omitted a field loads the same.

### Targets

An element of `targets`:

| Field | Type | Unit | Default | Engine property |
|---|---|---|---|---|
| `id` | string | | required, unique in the document | none (the link a `target` container uses) |
| `name` | string | | `""` | `InputTarget.TargetName` |
| `coordinates` | object | | required | `InputTarget.InputCoordinates`: `raHours` (int), `raMinutes` (int), `raSeconds` (double), `negativeDec` (bool), `decDegrees` (int), `decMinutes` (int), `decSeconds` (double); J2000 |
| `positionAngleDegrees` | number | degrees | `0` | `InputTarget.PositionAngle` (the legacy `Rotation` field is `360 - PositionAngle` and is not carried) |

Coordinates keep the engine's sexagesimal fields. The v1 body stores a target inside its `DeepSkyObjectContainer`; v2 hoists it so a client can list the targets without walking the tree.

### Common fields

| Field | On | Type | Default | Engine property |
|---|---|---|---|---|
| `kind` | every node | string | required | none (picks the engine class) |
| `name` | every node | string | the engine class's default name | `Name` |
| `errorBehavior` | instruction, container | `continue-on-error`, `skip-instruction-set-on-error`, `abort-on-error` | engine default | `SequenceItem.ErrorBehavior` (`InstructionErrorBehavior`) |
| `attempts` | instruction, container | int | engine default | `SequenceItem.Attempts` |
| `items` | container | array of instruction or container nodes | `[]` | `SequenceContainer.Items` |
| `conditions` | container | array of condition nodes | `[]` | `SequenceContainer.Conditions` |
| `triggers` | container | array of trigger nodes | `[]` | `SequenceContainer.Triggers` |
| `expanded` | container | bool | engine default | `SequenceContainer.IsExpanded`, editor state, kept so v1 round-trips |

The container's execution strategy is not a field: `sequential`, `target`, `smart-exposure` and `root` build `SequentialStrategy`; `parallel` builds `ParallelStrategy`. The v1 `Strategy.$type` token goes away on migration.

A node's `Parent` is never stored; the tree gives it. The v1 `$id` / `$ref` reference bookkeeping (`PreserveReferencesHandling.All`) is not carried: v2 has no shared nodes, and the writer emits each node once.

## Kind to engine class

The namespace for every class is `OpenAstroAra.Sequencer.<group>` as listed; the assembly is `OpenAstroAra.Sequencer`. This is every class `HeadlessSequencerFactory.WithDefaults` registers at the head this spec was written against (47 instructions, 5 conditions, 5 containers, 10 triggers). `DefaultConditions` skips `AboveHorizonCondition` and `AltitudeCondition` on a daemon without the astrometry natives; the kinds stay valid there, but a body naming them loads as a placeholder (today's behaviour, and the user sees the placeholder message below).

Units are the engine's. A unit that is not obvious is in the Fields column. Any field not listed for a kind is the engine's `[JsonProperty]` set for that class and follows the lowerCamelCase rule; follow-up (a) pins the exhaustive per-kind table in the validator.

### Containers

| `kind` | Engine class (`...Container`) | Fields (type, unit, default) |
|---|---|---|
| `root` | `SequenceRootContainer` | `pauseGate` (object, optional, runtime state of the pause gate, daemon-written). Exactly one, only at `root` |
| `sequential` | `SequentialContainer` | common fields only |
| `parallel` | `ParallelContainer` | common fields only |
| `target` | `DeepSkyObjectContainer` | `targetId` (string, required, an `id` in `targets`); `exposureInfoList` (array of `ExposureInfo`: `count` (int), `filter` (string), `exposureTime` (double, seconds), `gain` (int), `offset` (int), `imageType` (string), `binningX` (short), `binningY` (short), `roi` (double); default `[]`); `exposureInfoListExpanded` (bool, editor state) |
| `smart-exposure` | `SmartExposure` (namespace `...SequenceItem.Imaging`) | common fields only; the engine builds its own children as NINA's Smart Exposure does, and they travel in `items` and `triggers` |

`StartAreaContainer`, `TargetAreaContainer`, `EndAreaContainer` and NINA's `ConditionalContainer` exist as classes or NINA types but are not registered in the factory, so a v1 body naming them loads as `UnknownSequenceContainer` today. v2 has no `kind` for them (see NINA import).

### Instructions

| `kind` | Engine class | Namespace group | Fields (type, unit, default) |
|---|---|---|---|
| `annotation` | `Annotation` | `SequenceItem.Utility` | `text` (string) |
| `wait-for-user` | `WaitForUser` | `SequenceItem.Utility` | `text` (string); arms the root pause gate as a prompt |
| `wait-for-duration` | `WaitForTimeSpan` | `SequenceItem.Utility` | `time` (double, seconds, default `1`) |
| `wait-for-time` | `WaitForTime` | `SequenceItem.Utility` | `provider` (string, see Time sources, default `clock`), `hours` (int), `minutes` (int), `minutesOffset` (int), `seconds` (int) |
| `wait-for-sun-altitude` | `WaitForSunAltitude` | `SequenceItem.Utility` | `data` (`WaitLoopData` from `WaitForAltitudeBase`: altitude offset in degrees and the comparator) |
| `wait-until-safe` | `WaitUntilSafe` | `SequenceItem.SafetyMonitor` | none |
| `set-tracking` | `SetTracking` | `SequenceItem.Telescope` | `trackingMode` (`TrackingMode`, default `Sidereal`) |
| `unpark-mount` | `UnparkScope` | `SequenceItem.Telescope` | none |
| `park-mount` | `ParkScope` | `SequenceItem.Telescope` | none |
| `find-home-mount` | `FindHome` | `SequenceItem.Telescope` | none |
| `slew-radec` | `SlewScopeToRaDec` | `SequenceItem.Telescope` | `inherited` (bool, take the enclosing target), `coordinates` (as in Targets), `center` (bool or null; null = the profile's "center after slew") |
| `slew-altaz` | `SlewScopeToAltAz` | `SequenceItem.Telescope` | `coordinates`: `azDegrees`, `azMinutes`, `azSeconds`, `altDegrees`, `altMinutes`, `altSeconds` |
| `tracking-probe` | `TrackingProbe` | `SequenceItem.Telescope` | `rungs` (array of double, seconds), `maxExposure` (double, seconds), `eccentricityTolerance` (double), `priorSeconds` (double or null), `ceilingSeconds` (double or null) |
| `move-focuser-absolute` | `MoveFocuserAbsolute` | `SequenceItem.Focuser` | `position` (int, steps) |
| `move-focuser-relative` | `MoveFocuserRelative` | `SequenceItem.Focuser` | `relativePosition` (int, steps) |
| `move-focuser-by-temperature` | `MoveFocuserByTemperature` | `SequenceItem.Focuser` | `slope` (double, steps per degree C, default `1`), `intercept` (double, steps), `absolute` (bool, default `true`) |
| `capture` | `TakeExposure` | `SequenceItem.Imaging` | `exposureTime` (double, seconds), `gain` (int, default `-1` = camera's current), `offset` (int, default `-1` = camera's current), `binning` (`{ "x": 1, "y": 1 }`, default 1x1), `imageType` (`LIGHT`, `DARK`, `FLAT`, `BIAS`, ..., default `LIGHT`), `exposureCount` (int, runtime counter, daemon-written), `calibrationTarget` (string or absent), `calibrationNight` (`yyyy-MM-dd` or absent) |
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
| `loop-while-above-horizon` | `AboveHorizonCondition` | `Conditions` | `hasDsoParent` (bool), `data` (`WaitLoopData` from `LoopForAltitudeBase`: offset in degrees, comparator) |
| `loop-while-altitude` | `AltitudeCondition` | `Conditions` | `hasDsoParent` (bool), `data` (`WaitLoopData` from `LoopForAltitudeBase`: altitude in degrees, comparator) |

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
| `originalType` | string | the NINA `$type` token as found, or the v1 token |
| `originalPayload` | object | the source node, kept so nothing is lost and a later Ara release can retry it |
| `reason` | string | why it was not mapped |

A placeholder is written only by the importer and the migration, never by a client. The validator rejects an `unknown` node on upload from a client; the daemon keeps it in the library as it keeps it today. The engine behaviour is today's: an `unknown` instruction throws `SequenceItemSkippedException` ("Unknown instruction will be skipped - &lt;name&gt;"), and the placeholder's validation never passes (`Validate()` returns `false`). A failed condition check on an `unknown` condition ends its container's loop, which is how an unregistered altitude condition skipped a whole target block before `AboveHorizonCondition` was registered (`HeadlessSequencerFactory.DefaultConditions`).

### Ara-only kind: `glover-exposure`

See the adaptive section below. It has no engine class today; follow-up (d) adds exactly one.

## NINA JSON import

Import reads a NINA `.json` and writes a v2 document. It is the one place a `$type` token is read. It never writes NINA JSON back.

1. **Detect.** A body with a top-level `$type` whose value resolves through `NinaTypeRemapper.ResolveType`, and no `schemaVersion`, is NINA JSON. A body with `schemaVersion` `openastroara-sequence-v1` is a v1 body and goes to migration, not import. Any other body is rejected with "This file is not a NINA sequence." (HTTP 422).
2. **Resolve each `$type`.** The remap is the one `NinaTypeRemapper` does today: `NINA.Sequencer` to `OpenAstroAra.Sequencer`, `NINA.Astrometry` to `OpenAstroAra.Astrometry`, `NINA.Core` to `OpenAstroAra.Core`, then the assembly-name fallbacks. The five `NINA.Plugins.Connector` instructions (`ConnectAllEquipment`, `ConnectEquipment`, `DisconnectEquipment`, `DisconnectAllEquipment`, `SwitchProfile`) map to the same-named Ara classes, as `SequenceItemCreationConverter.PluginMergeMigration` does. Reading never sets `TypeNameHandling` and never builds a type outside the factory's registered classes ([ADR 0018](../adr/0018-a-sequence-is-stored-in-ninas-json-format.md) rule 2 carries over to the importer).
3. **Map to `kind`.** The resolved class gives the `kind` through the tables above; each `[JsonProperty]` becomes the lowerCamelCase field. `ImageType` `DARKFLAT` becomes `DARK`, as `SequenceItemCreationConverter` does today. A NINA `Strategy.$type` is dropped (the `kind` decides it). A `SelectedProvider.$type` becomes a `provider` string. A `DeepSkyObjectContainer`'s `Target` is hoisted into `targets` and the container gets a `targetId`. `$id` / `$ref` pairs are expanded: a `$ref` to a node already seen is rejected as "shared node", which NINA files do not produce for the types Ara supports.
4. **Supported constructs import losslessly**: every NINA class the tables list, with every field the class carries. Import then export to NINA is not offered, so lossless means "nothing the engine runs is dropped or changed".
5. **Not supported: dropped or rejected.**

| NINA construct | What import does |
|---|---|
| a `$type` the factory does not register (a NINA instruction Ara does not ship, any third-party plugin type, MGEN, `StartAreaContainer`, `TargetAreaContainer`, `EndAreaContainer`, `ConditionalContainer`, `IfContainer`-style containers) | the node becomes a placeholder (`kind: "unknown"`) with `originalType`, `originalPayload` and `reason`; its children are inside `originalPayload` and are not imported; the import goes on |
| a registered `$type` whose constructor fails (the factory throws) | the same placeholder, `reason` says the node could not be built |
| a plugin type under `NINA.Plugins.*` other than the five Connector instructions | the same placeholder |
| a node with no `$type` | the same placeholder, `originalType` empty |
| a device id (`SelectedDevice`, `AlpacaDeviceNumber`) that names a NINA ASCOM device | kept as found; the importer adds a warning `equipment_unmatched` (the user reassigns it in the editor) |
| malformed JSON, a body that is not an object, or a root that is not a container | the whole import is rejected; nothing is written |

6. **What the user is told.** The import reply carries `warnings`, one per placeholder and per unmatched device. The text of a placeholder warning is exactly:

   `"<originalType>" is not supported by OpenAstro Ara and was not imported. It stays in the sequence as a placeholder that is skipped when the sequence runs.`

   The placeholder node itself shows, in the editor and in the sequence's validation list, today's engine text unchanged: the name `<Unknown Instruction - <originalType>> ` and the issue "Unknown instruction detected! Please double check your sequence and inspect which instructions became invalid. Furthermore validate that everything is up to date." (`LblUnknownInstruction`, `LblUnknownInstructionValidation`). When the engine reaches it, the run log says "Unknown instruction will be skipped - <name>". The client banner counts the warnings ("3 issues need attention"), as [`design/PORT_PLAYBOOK.md`](../../design/PORT_PLAYBOOK.md) §38.4 describes. An import with at least one placeholder still saves; it cannot start until the user removes or replaces each placeholder, because a sequence with a placeholder fails its validation (`Validate()` is `false`).
7. **Provenance.** The importer sets `metadata.importedFrom` to `{ "format": "nina", "fileName": "<name>", "importedUtc": "<ISO 8601>" }`. The source file is kept under `sequences/imported/from-nina-YYYY-MM-DD/` ([`design/PORT_PLAYBOOK.md`](../../design/PORT_PLAYBOOK.md) §38.2) so a placeholder can be retried after an Ara upgrade.
8. **No Glover.** NINA has no equivalent of `glover-exposure`, so import never creates one; a NINA `TakeExposure` becomes a `capture`.

## Migration of stored v1 bodies

A v1 body is a NINA-shaped `$type` tree with `"schemaVersion": "openastroara-sequence-v1"`. Migration turns it into a v2 document with the same function the importer uses for the mapping (steps 2 and 3 above), run on the body already stored, with no change to its meaning.

1. **When.** Once, at daemon startup, after the profile directory is open and before the sequence service accepts a request, for every `sequences/library/*.json`, then for the checkpoint. It is not run lazily on read, so a run never meets a v1 body. A body uploaded as v1 after the upgrade is rejected by the validator (follow-up (a) decides whether a v1 upload is migrated on the way in or refused; this spec says refused, 422, "This daemon stores sequence v2. Upload a v2 document.").
2. **Library bodies.** The `Body` of each library file is migrated and the file is rewritten atomically (temp file and rename, as `ActiveSequenceCheckpoint` writes). Before the first rewrite of a file, the v1 file is copied to `sequences/library/.v1-backup/<id>.json`, once; a later run never overwrites a backup. Block and target names are kept exactly, because the client and the rig find a Simple Mode target block by its name, and the end-of-night `Flats` and `Calibration` blocks by theirs ([ADR 0010](../adr/0010-simple-mode-queue-is-one-sequence-on-the-rig.md) rules 2 and 4).
3. **Active checkpoint.** `sequences/active/current.json` holds a `SequenceRunStateDto` (the `SequenceId` of the library sequence and run counters), not a body, so it needs no body rewrite. Migration still reads it: the run it names resumes only from the migrated library body, and the reconciler's rule is unchanged (a checkpoint found at startup is never auto-resumed; [`design/PORT_PLAYBOOK.md`](../../design/PORT_PLAYBOOK.md) §28.2). If a checkpoint ever carries a body, the same function migrates it in place. A checkpoint whose sequence failed migration is quarantined to `current.json.corrupt.<unix-ts>`, the way a damaged checkpoint is today.
4. **Mechanical mapping.** Each `$type` goes to its `kind` through the tables; each property goes to its lowerCamelCase field; `Strategy` is dropped; `TriggerRunner.Items` becomes `run`; `SelectedProvider` becomes `provider`; each `DeepSkyObjectContainer.Target` is hoisted to `targets` with a fresh id (`t1`, `t2`, in tree order) and the container gets `targetId`; `$id` / `$ref` are expanded; the v1 `schemaVersion` is replaced. Runtime counters (`exposureCount`, `completedIterations`) are kept. A node whose `$type` the migration cannot map becomes a placeholder exactly as in import (the user keeps what they had; the daemon logs it).
5. **Idempotent.** A body that already says `openastroara-sequence-v2` is left alone, byte for byte: the migration checks `schemaVersion` first and writes nothing. Running it twice on a v1 body gives the first run's result; a crash between the backup copy and the atomic rewrite leaves the v1 file in place, so the next startup redoes it.
6. **A body that fails.** Malformed JSON, a non-object body, or a root that is not a container: the file is not rewritten and is not deleted. The daemon logs the file name and the error (the file name escaped, CWE-117), marks the sequence unreadable in the list (the sequence service returns it with a `migration_failed` problem, RFC 7807, on GET), and posts one notification: "Sequence '<name>' could not be updated to the new format. It was left unchanged." The other files still migrate. A daemon never refuses to start because a sequence failed. A sequence that failed cannot start until the user re-uploads it.
7. **Downgrade.** There is none. A v2 library is not read by a v1 daemon; the `.v1-backup` copies are the way back.

## Adaptive exposure (Glover)

The adaptive runtime exposure measures the sky background from the live frames and sets the sub length during the run. Today's Glover model lives in planning ([`SensorQeLibrary`](../../OpenAstroAra.Server/Services/SensorQeLibrary.cs) feeds the client's optimal-sub calculator); this node brings it into the run.

- **`kind`**: `glover-exposure` (instruction). **No NINA equivalent**, so no engine class exists for it. Follow-up (d) adds one new class under `OpenAstroAra.Sequencer.SequenceItem.Imaging` and registers it in `HeadlessSequencerFactory`. Everything else in this spec stays 1:1 with a NINA class; this is the one exception.
- **Behaviour**: a `capture` that, instead of a fixed `exposureTime`, measures the sky background (median ADU of the last `measureFrames` light frames, converted with the profile's gain and electrons-per-ADU) and sets the next sub to the Glover optimum, clamped to `[minSubSeconds, maxSubSeconds]`. It reads the frames through the run's `IFrameQualityFeed`, the seam `UnguidedExposureGuard` uses. With fewer than `measureFrames` frames so far it exposes `startSubSeconds`.
- **Fields** (proposed, follow-up (d) fixes them): `startSubSeconds` (double, seconds, required), `minSubSeconds` (double, seconds), `maxSubSeconds` (double, seconds), `measureFrames` (int, default `3`), `swampFactor` (double, how far sky noise must exceed read noise), `frameCount` (int), plus the `capture` fields `gain`, `offset`, `binning` and `imageType` (`LIGHT` only).
- **Import**: never produces it (see NINA JSON import, step 8).
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
      "positionAngleDegrees": 0
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

Follow-up (a) writes the validator. The rules it must hold, beyond today's `SequenceSchemaValidator` ones (object body, schema version, at least one capturable instruction reachable from root):

- every `kind` is in the tables above, or the daemon has the Glover class for `glover-exposure`;
- `root` is the only `root`, and a `target`'s `targetId` is an `id` in `targets`;
- a `capture`, `flat-panel-flats` or `sky-flats` node or a `glover-exposure` counts as capturable;
- no unknown fields (a typo is a 422 naming the node path);
- a client upload carries no `unknown` node.

## Follow-up issues

Four issues, filed after the maintainer signs this spec off. Each is a separate PR against `master`.

### (a) Schema and validator

**Summary.** Give v2 a machine-readable schema and a server validator, and make v2 the format the daemon stores. This supersedes [ADR 0018](../adr/0018-a-sequence-is-stored-in-ninas-json-format.md): its rules 1 to 3 (NINA's schema with `$type` on every node, `TypeNameHandling` never set on read, schema version `v1`) change. The PR needs an ADR change, so it needs a board card per the ADR gate; the record's "How to reverse" section already lists this move.

**Desired outcome.** A sequence is written and read as a v2 document by one writer. `SequenceSchemaValidator.SchemaVersion` is `openastroara-sequence-v2` and the validator holds the Validation rules above. A `kind` to engine class table is declared once in the Sequencer and the factory builds from it. The client's sequence models and the run builders ([ADR 0010](../adr/0010-simple-mode-queue-is-one-sequence-on-the-rig.md)'s `imaging_run_body.dart`) emit v2. The daemon no longer reads a `$type` token outside the importer and the migration.

**Acceptance criteria.**
- ADR 0018 is superseded by a new record that states the v2 rules, with the board's `ADR change:` card accepted for the head sha.
- A document in this spec's Example validates; each rule in Validation has a test that fails when the rule is removed.
- `kind` to class coverage test: every class `WithDefaults` registers has exactly one `kind`, and every `kind` builds that class.
- A v2 body round-trips through the engine tree and back byte-identically (field order fixed).
- The client's generated bodies (Simple Mode blocks, `Flats`, `Calibration`) are v2 and keep their block names.
- `SequenceSchemaValidatorTest`, the round-trip tests and the Simple Mode plan tests are updated, not deleted.

**Out of scope.** The NINA importer (b), the migration (c), the Glover node (d), Native AOT publishing, any engine change.

### (b) NINA importer

**Summary.** Replace the `$type`-reading path of NINA import with the one-way importer in "NINA JSON import".

**Desired outcome.** `POST /api/v1/sequences/import` takes a NINA `.json` and returns a v2 document plus `warnings`. Supported constructs import losslessly; unsupported ones become placeholders with the exact warning text; nothing else changes the daemon's behaviour.

**Acceptance criteria.**
- A fixture set of real NINA files (a target block, a Smart Exposure with triggers, the Connector plugin instructions, a plugin instruction Ara does not ship) imports with the node counts and field values asserted.
- The warning text for a placeholder equals the sentence in this spec, and the placeholder shows the `LblUnknownInstruction` and `LblUnknownInstructionValidation` text.
- `DARKFLAT` becomes `DARK`; the five Connector instructions map; `StartAreaContainer` becomes a placeholder.
- Malformed JSON, a non-object body and a non-container root are rejected and write nothing.
- The importer never sets `TypeNameHandling` and builds only registered classes.
- `NinaImportFidelityTest` and `JsonCreationConverterRemapTest` are ported, not deleted.

**Out of scope.** NINA export, importing plugin instructions, equipment matching beyond the `equipment_unmatched` warning, the client import screen.

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

**Out of scope.** Downgrade, migrating bodies outside `sequences/library`, the importer (b), schema changes (a).

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

- The per-kind field table lists the engine properties this spec could confirm; follow-up (a) pins the exhaustive table against `[JsonProperty]` in code, and a field it finds that is not listed here is added to this spec in the same PR.
- A document with a `$ref` shared node is rejected on import; no NINA type Ara supports produces one.
- No NINA export, by decision.
- Migration cannot restore a v1 body's `$id` numbering; the v1 backup is the only way back to it.
