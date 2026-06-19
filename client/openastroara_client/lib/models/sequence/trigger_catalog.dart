/// §38 sequence-editor trigger catalog — the triggers a user can add to a
/// container. A trigger fires between instructions while the container runs (the
/// canonical one being the meridian flip). Each [TriggerDef] mirrors a real C#
/// `OpenAstroAra.Sequencer.Trigger` class so a built node deserialises and runs
/// on the daemon unchanged.
///
/// Grounded against `OpenAstroAra.Sequencer/Trigger/*.cs`: the abstract
/// `SequenceTrigger` base serialises `Parent` and a `TriggerRunner` (a
/// `SequentialContainer`, constructed `new SequentialContainer()`), so a built
/// trigger is `$type` + its own fields + `Parent: null` + an empty
/// `TriggerRunner` container (reused from the instruction catalog so its shape
/// stays identical to a hand-added container).
///
/// Only `MeridianFlipTrigger` is listed for now — it carries no serialized
/// fields of its own (its flip parameters are read from the profile, not the
/// sequence). `ReconnectTrigger` needs its `SelectedDevice` device-name set
/// grounded, so it's deferred to its own slice (tracked in design/PORT_TODO.md)
/// rather than guessed.
library;

import 'package:flutter/material.dart';

import 'instruction_catalog.dart'
    show InstructionField, deepCloneJson, instructionForType;

/// The `$type` of the `SequentialContainer` used for a trigger's `TriggerRunner`
/// — built via the instruction catalog so the runner is shaped exactly like any
/// other container the editor produces.
const String _sequentialContainerType =
    'OpenAstroAra.Sequencer.Container.SequentialContainer, OpenAstroAra.Sequencer';

/// One trigger in the container's "add trigger" picker.
@immutable
class TriggerDef {
  /// The assembly-qualified Newtonsoft `$type`.
  final String type;

  /// Picker/row label (e.g. "Meridian Flip").
  final String label;

  /// The picker/row icon.
  final IconData icon;

  /// The editable + runtime-managed fields, in display order. Reuses
  /// [InstructionField]; `MeridianFlipTrigger` has none.
  final List<InstructionField> fields;

  const TriggerDef({
    required this.type,
    required this.label,
    required this.icon,
    this.fields = const [],
  });

  /// Base keys `build` writes itself — a field may not reuse one (it would be
  /// silently clobbered below).
  static const Set<String> _reservedKeys = {'Parent', 'TriggerRunner'};

  /// A fresh raw-body trigger node: `$type`, each field at its (deep-cloned)
  /// default, the base `Parent: null`, and a fresh empty `TriggerRunner`
  /// (`SequentialContainer`). The runner is built from the instruction catalog
  /// so it shares nothing with the catalog and is shaped exactly like a
  /// user-added container.
  Map<String, dynamic> build() {
    final keys = fields.map((f) => f.key).toSet();
    if (keys.length != fields.length) {
      throw StateError('TriggerDef($label) has duplicate field keys');
    }
    for (final f in fields) {
      if (_reservedKeys.contains(f.key)) {
        throw StateError(
            'TriggerDef($label) field "${f.key}" collides with a reserved base key');
      }
    }
    final containerDef = instructionForType(_sequentialContainerType) ??
        (throw StateError(
            'TriggerDef: SequentialContainer not found in the instruction catalog'));
    final node = <String, dynamic>{r'$type': type};
    for (final f in fields) {
      node[f.key] = deepCloneJson(f.defaultValue);
    }
    node['Parent'] = null;
    node['TriggerRunner'] = containerDef.build();
    return node;
  }
}

/// The triggers, in display order.
const List<TriggerDef> triggerCatalog = [
  // The meridian flip — the canonical trigger. No serialized fields: the flip
  // timing/side-of-pier parameters live in the profile, not the sequence.
  TriggerDef(
    type: 'OpenAstroAra.Sequencer.Trigger.MeridianFlip.MeridianFlipTrigger, OpenAstroAra.Sequencer',
    label: 'Meridian Flip',
    icon: Icons.flip_camera_android_outlined,
  ),
];

/// `$type` → [TriggerDef] index. Throws (in release too) on a duplicate `$type`
/// rather than last-wins-shadowing it, matching the instruction/condition catalogs.
final Map<String, TriggerDef> _triggersByType = () {
  final byType = <String, TriggerDef>{};
  for (final def in triggerCatalog) {
    if (byType.containsKey(def.type)) {
      throw StateError('duplicate TriggerDef \$type: ${def.type}');
    }
    byType[def.type] = def;
  }
  return Map<String, TriggerDef>.unmodifiable(byType);
}();

/// The [TriggerDef] whose `$type` equals [type], or null if not catalogued
/// (e.g. a trigger the editor can display but not yet construct).
TriggerDef? triggerForType(String type) => _triggersByType[type];
