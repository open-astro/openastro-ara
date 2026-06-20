/// §38 sequence-editor loop-condition catalog — the conditions a user can add
/// to a container to control how long / how many times it loops. Each
/// [ConditionDef] mirrors a real C# `OpenAstroAra.Sequencer.Conditions` class so
/// a built node deserialises and runs on the daemon unchanged.
///
/// Grounded against the server classes under `OpenAstroAra.Sequencer/Conditions/`
/// and the daemon's `narrowband-shoo.json` template: the assembly-qualified
/// `$type`, the `[JsonProperty]` field keys, and the constructor defaults. The
/// abstract `SequenceCondition` base serialises only `Parent` (its Name/Icon/…
/// are plain get/set, not `[JsonProperty]`, so they're omitted under Json.NET
/// OptIn), so a built condition is `$type` + its own fields + `Parent: null` —
/// no `ErrorBehavior`/`Attempts` (those are `SequenceItem` fields).
///
/// The pure-scalar conditions plus the altitude conditions `AltitudeCondition`/
/// `AboveHorizonCondition`, whose `Data` is a nested `WaitLoopData` object
/// (coordinates target + degrees offset + comparator). `TimeCondition` (needs a
/// `SelectedProvider` object) is added in its own slice.
library;

import 'package:flutter/material.dart';

import 'instruction_catalog.dart'
    show
        InstructionField,
        InstructionFieldType,
        checkNoReservedFieldKeys,
        deepCloneJson,
        defaultCoordinates;

/// The assembly-qualified `$type` of the altitude conditions' nested `Data`
/// object (`LoopForAltitudeBase.Data`). Grounded against
/// `OpenAstroAra.Sequencer/SequenceItem/Utility/WaitLoopData.cs`.
const String waitLoopDataType =
    'OpenAstroAra.Sequencer.SequenceItem.Utility.WaitLoopData, OpenAstroAra.Sequencer';

/// The user-selectable `ComparisonOperator` values for an altitude condition →
/// display label. NINA only offers `LessThan` (1) and `GreaterThan` (3) here
/// (`WaitLoopData.ComparisonOperators` drops the `*OrEqual` / `EQUALS` /
/// `NotEqual` variants); the integer values match
/// `OpenAstroAra.Core/Enum/ComparisonOperatorEnum.cs`.
const Map<int, String> altitudeComparators = {
  1: 'Less than (<)',
  3: 'Greater than (>)',
};

/// `AltitudeCondition.Data` default: target altitude 30°, `LessThan` (loop until
/// the target drops below 30°), against a zeroed `InputCoordinates`. The nested
/// [defaultCoordinates] is const; [ConditionDef.build] deep-clones the whole
/// `Data` so built nodes never share it.
const Map<String, dynamic> altitudeConditionData = {
  r'$type': waitLoopDataType,
  'Coordinates': defaultCoordinates,
  'Offset': 30.0,
  'Comparator': 1,
};

/// `AboveHorizonCondition.Data` default: offset 0° above the (custom) horizon,
/// `GreaterThan`, against a zeroed `InputCoordinates`.
const Map<String, dynamic> aboveHorizonConditionData = {
  r'$type': waitLoopDataType,
  'Coordinates': defaultCoordinates,
  'Offset': 0.0,
  'Comparator': 3,
};

/// `TimeCondition.SelectedProvider` options: display label → the provider's
/// assembly-qualified `$type`, or null for "Custom time" (the absolute
/// Hours/Minutes/Seconds clock time). Grounded against
/// `OpenAstroAra.Sequencer/Utility/DateTimeProvider/*.cs`. Insertion order is the
/// dropdown order (Custom first). The provider objects carry no `[JsonProperty]`
/// fields, so each is just `{$type}`.
const Map<String, String?> timeProviders = {
  'Custom time': null,
  'Sunset': 'OpenAstroAra.Sequencer.Utility.DateTimeProvider.SunsetProvider, OpenAstroAra.Sequencer',
  'Sunrise': 'OpenAstroAra.Sequencer.Utility.DateTimeProvider.SunriseProvider, OpenAstroAra.Sequencer',
  'Civil Dusk': 'OpenAstroAra.Sequencer.Utility.DateTimeProvider.CivilDuskProvider, OpenAstroAra.Sequencer',
  'Civil Dawn': 'OpenAstroAra.Sequencer.Utility.DateTimeProvider.CivilDawnProvider, OpenAstroAra.Sequencer',
  'Nautical Dusk': 'OpenAstroAra.Sequencer.Utility.DateTimeProvider.NauticalDuskProvider, OpenAstroAra.Sequencer',
  'Nautical Dawn': 'OpenAstroAra.Sequencer.Utility.DateTimeProvider.NauticalDawnProvider, OpenAstroAra.Sequencer',
  'Astronomical Dusk': 'OpenAstroAra.Sequencer.Utility.DateTimeProvider.DuskProvider, OpenAstroAra.Sequencer',
  'Astronomical Dawn': 'OpenAstroAra.Sequencer.Utility.DateTimeProvider.DawnProvider, OpenAstroAra.Sequencer',
  'Meridian': 'OpenAstroAra.Sequencer.Utility.DateTimeProvider.MeridianProvider, OpenAstroAra.Sequencer',
};

/// Build a `SelectedProvider` value from a [timeProviders] label: null (custom
/// time) or `{$type}` for a sky-event provider. The dropdown only ever passes a
/// known key; the assert catches a future catalog typo (a label not in the map
/// would otherwise silently fall back to custom time).
Map<String, dynamic>? timeProviderValue(String label) {
  assert(timeProviders.containsKey(label), 'unknown time-provider label: $label');
  final type = timeProviders[label];
  return type == null ? null : <String, dynamic>{r'$type': type};
}

/// The [timeProviders] label for a stored `SelectedProvider` value (null or a
/// `{$type}` map) — 'Custom time' for null/unrecognised.
String timeProviderLabel(Object? value) {
  final type = value is Map ? value[r'$type'] : null;
  for (final entry in timeProviders.entries) {
    if (entry.value == type) return entry.key;
  }
  return 'Custom time';
}

/// `enabledWhen` for a `TimeCondition`'s Hours/Minutes/Seconds: they're only
/// user-meaningful in **Custom time** mode. Driven by [timeProviderLabel] (not a
/// raw `== null` check) so it agrees with the When dropdown — an unrecognised
/// stored `$type` reads as 'Custom time' there, and the H/M/S stay enabled to
/// match (rather than greying out under a label that says Custom time). A
/// top-level function so it's const-constructible.
bool timeConditionUsesClock(Map<String, dynamic> node) =>
    timeProviderLabel(node['SelectedProvider']) == 'Custom time';

/// The inverse of [timeConditionUsesClock] — `enabledWhen` for `MinutesOffset`,
/// which the daemon applies ONLY under a sky-event provider (`TimeCondition`'s
/// `UpdateTime` adds it to `SelectedProvider.GetDateTime`); in Custom-time mode
/// it's inert, so the editor greys it out.
bool timeConditionUsesProvider(Map<String, dynamic> node) =>
    !timeConditionUsesClock(node);

/// One loop condition in the container's "add condition" picker.
@immutable
class ConditionDef {
  /// The assembly-qualified Newtonsoft `$type`.
  final String type;

  /// Picker/row label (e.g. "Loop (iterations)").
  final String label;

  /// The picker/row icon.
  final IconData icon;

  /// The editable + runtime-managed fields, in display order. Reuses
  /// [InstructionField] — a condition's fields are the same scalar shapes an
  /// instruction's are (all current conditions are integer-valued).
  final List<InstructionField> fields;

  const ConditionDef({
    required this.type,
    required this.label,
    required this.icon,
    this.fields = const [],
  });

  /// A fresh raw-body condition node: `$type`, each field at its (deep-cloned)
  /// default, plus the base `Parent: null`. Fully mutable and sharing nothing
  /// with the catalog so the editor can mutate it freely.
  ///
  /// Defaults are deep-cloned via [deepCloneJson] — the same helper
  /// [InstructionDef.build] uses — so a future object-valued condition default
  /// (e.g. a `TimeCondition` provider) can't silently share mutable state across
  /// built nodes. (Today's conditions are all scalar ints, where the clone is a
  /// no-op, but cloning unconditionally keeps the two build paths identical.)
  Map<String, dynamic> build() {
    final keys = fields.map((f) => f.key).toSet();
    if (keys.length != fields.length) {
      throw StateError('ConditionDef($label) has duplicate field keys');
    }
    // A field may not reuse a base key build() writes itself.
    checkNoReservedFieldKeys('ConditionDef($label)', fields, const {r'$type', 'Parent'});
    final node = <String, dynamic>{r'$type': type};
    for (final f in fields) {
      node[f.key] = deepCloneJson(f.defaultValue);
    }
    node['Parent'] = null;
    return node;
  }
}

/// The loop conditions, in display order.
const List<ConditionDef> conditionCatalog = [
  // Loop a container a fixed number of times — the most common condition.
  // CompletedIterations is a [JsonProperty] runtime counter (like TakeExposure's
  // ExposureCount): emitted at its default but hidden from the editor.
  ConditionDef(
    type: 'OpenAstroAra.Sequencer.Conditions.LoopCondition, OpenAstroAra.Sequencer',
    label: 'Loop (iterations)',
    icon: Icons.repeat,
    fields: [
      InstructionField('Iterations', 'Iterations', InstructionFieldType.integer,
          defaultValue: 2, min: 1),
      InstructionField('CompletedIterations', 'Completed', InstructionFieldType.integer,
          defaultValue: 0, editable: false),
    ],
  ),
  // Run a container for a fixed wall-clock duration (HH:MM:SS). Default 1 minute
  // (the C# constructor default), all three are `[JsonProperty] public int`.
  ConditionDef(
    type: 'OpenAstroAra.Sequencer.Conditions.TimeSpanCondition, OpenAstroAra.Sequencer',
    label: 'For a duration',
    icon: Icons.timelapse_outlined,
    fields: [
      InstructionField('Hours', 'Hours', InstructionFieldType.integer, defaultValue: 0, min: 0),
      InstructionField('Minutes', 'Minutes', InstructionFieldType.integer,
          defaultValue: 1, min: 0, max: 59),
      InstructionField('Seconds', 'Seconds', InstructionFieldType.integer,
          defaultValue: 0, min: 0, max: 59),
    ],
  ),
  // Loop a container until the target's altitude crosses a threshold. Data is a
  // nested WaitLoopData (target coordinates + degrees offset + comparator);
  // HasDsoParent is a [JsonProperty] the daemon recomputes from the parent DSO
  // context on load, so it's emitted at its default but hidden from the editor.
  ConditionDef(
    type: 'OpenAstroAra.Sequencer.Conditions.AltitudeCondition, OpenAstroAra.Sequencer',
    label: 'Until altitude',
    icon: Icons.height,
    fields: [
      InstructionField('Data', 'Target', InstructionFieldType.waitLoopData,
          defaultValue: altitudeConditionData),
      InstructionField('HasDsoParent', 'Has DSO parent', InstructionFieldType.boolean,
          defaultValue: false, editable: false),
    ],
  ),
  // Loop a container until the target is above the (custom) horizon. Same shape
  // as AltitudeCondition; the offset is added to the horizon altitude.
  ConditionDef(
    type: 'OpenAstroAra.Sequencer.Conditions.AboveHorizonCondition, OpenAstroAra.Sequencer',
    label: 'Until above horizon',
    icon: Icons.terrain_outlined,
    fields: [
      InstructionField('Data', 'Target', InstructionFieldType.waitLoopData,
          defaultValue: aboveHorizonConditionData),
      InstructionField('HasDsoParent', 'Has DSO parent', InstructionFieldType.boolean,
          defaultValue: false, editable: false),
    ],
  ),
  // Run a container until a clock time or a sky event (sunset, civil dusk, …).
  // Hours/Minutes/Seconds are the absolute time when SelectedProvider is null
  // (Custom time); for a sky-event provider they're computed at runtime, and
  // MinutesOffset shifts the result. All four are `[JsonProperty] public int`.
  ConditionDef(
    type: 'OpenAstroAra.Sequencer.Conditions.TimeCondition, OpenAstroAra.Sequencer',
    label: 'Until a time',
    icon: Icons.schedule_outlined,
    fields: [
      InstructionField('SelectedProvider', 'When', InstructionFieldType.timeProvider,
          defaultValue: null),
      // H/M/S are the clock time only in Custom-time mode; under a sky-event
      // provider the daemon computes them, so they grey out (enabledWhen).
      InstructionField('Hours', 'Hour', InstructionFieldType.integer,
          defaultValue: 0, min: 0, max: 23, enabledWhen: timeConditionUsesClock),
      InstructionField('Minutes', 'Minute', InstructionFieldType.integer,
          defaultValue: 0, min: 0, max: 59, enabledWhen: timeConditionUsesClock),
      InstructionField('Seconds', 'Second', InstructionFieldType.integer,
          defaultValue: 0, min: 0, max: 59, enabledWhen: timeConditionUsesClock),
      // Deliberately unbounded (and signed via the free integer field): a
      // sky-event offset is a relative shift in either direction (e.g. -15 =
      // "15 min before sunset"); NINA imposes no ceiling on it. Greyed in
      // Custom-time mode — the daemon only applies it under a provider.
      InstructionField('MinutesOffset', 'Offset (min)', InstructionFieldType.integer,
          defaultValue: 0, enabledWhen: timeConditionUsesProvider),
    ],
  ),
];

/// `$type` → [ConditionDef] index. Throws (in release too) on a duplicate
/// `$type` rather than last-wins-shadowing it, matching the instruction catalog.
final Map<String, ConditionDef> _conditionsByType = () {
  final byType = <String, ConditionDef>{};
  for (final def in conditionCatalog) {
    if (byType.containsKey(def.type)) {
      throw StateError('duplicate ConditionDef \$type: ${def.type}');
    }
    byType[def.type] = def;
  }
  return Map<String, ConditionDef>.unmodifiable(byType);
}();

/// The [ConditionDef] whose `$type` equals [type], or null if not catalogued
/// (e.g. a condition the editor can display but not yet construct).
ConditionDef? conditionForType(String type) => _conditionsByType[type];
