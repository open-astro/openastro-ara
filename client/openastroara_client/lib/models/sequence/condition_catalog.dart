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
/// Only the pure-scalar conditions are listed. `TimeCondition` (needs a
/// `SelectedProvider` object) and the altitude conditions `AltitudeCondition`/
/// `AboveHorizonCondition` (need a nested `WaitLoopData` object) are intentionally
/// deferred to their own slices rather than faked (tracked in design/PORT_TODO.md).
library;

import 'package:flutter/material.dart';

import 'instruction_catalog.dart'
    show InstructionField, InstructionFieldType, checkNoReservedFieldKeys, deepCloneJson;

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
      InstructionField('Iterations', 'Iterations', InstructionFieldType.integer, defaultValue: 2),
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
      InstructionField('Hours', 'Hours', InstructionFieldType.integer, defaultValue: 0),
      InstructionField('Minutes', 'Minutes', InstructionFieldType.integer, defaultValue: 1),
      InstructionField('Seconds', 'Seconds', InstructionFieldType.integer, defaultValue: 0),
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
