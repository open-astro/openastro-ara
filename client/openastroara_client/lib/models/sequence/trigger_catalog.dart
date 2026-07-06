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
/// `MeridianFlipTrigger` carries no serialized fields of its own (its flip
/// parameters are read from the profile, not the sequence). `ReconnectTrigger`
/// carries a `SelectedDevice` string — one of the device names enumerated in the
/// C# `ConnectEquipment` instruction ([reconnectDeviceNames]; "Telescope" was
/// renamed "Mount").
library;

import 'package:flutter/material.dart';

import 'instruction_catalog.dart'
    show
        InstructionField,
        InstructionFieldType,
        checkNoReservedFieldKeys,
        deepCloneJson,
        instructionForType,
        sequentialContainerType;

/// The device names `ReconnectTrigger.SelectedDevice` accepts, grounded against
/// `OpenAstroAra.Sequencer/SequenceItem/Connect/ConnectEquipment.cs` (exact
/// casing/spelling). The daemon migrates a legacy "Telescope" value to "Mount".
const List<String> reconnectDeviceNames = [
  'Camera',
  'Filter Wheel',
  'Focuser',
  'Rotator',
  'Mount',
  'Guider',
  'Switch',
  'Flat Panel',
  'Weather',
  'Dome',
  'Safety Monitor',
];

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
  /// silently clobbered below). Includes `$type`, which is written before the
  /// field loop, so a field keyed `$type` would overwrite the discriminator.
  static const Set<String> _reservedKeys = {
    r'$type',
    'Parent',
    'TriggerRunner',
  };

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
    checkNoReservedFieldKeys('TriggerDef($label)', fields, _reservedKeys);
    final containerDef =
        instructionForType(sequentialContainerType) ??
        (throw StateError(
          'TriggerDef: SequentialContainer not found in the instruction catalog',
        ));
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
/// The assembly-qualified `$type` of the Autofocus-After-Exposures trigger —
/// the single source of truth, reused by the Planning tab's imaging-run
/// builder for the AF cadence.
const String autofocusAfterExposuresType =
    'OpenAstroAra.Sequencer.Trigger.Autofocus.AutofocusAfterExposures, OpenAstroAra.Sequencer';

const List<TriggerDef> triggerCatalog = [
  // The meridian flip — the canonical trigger. No serialized fields: the flip
  // timing/side-of-pier parameters live in the profile, not the sequence.
  TriggerDef(
    type:
        'OpenAstroAra.Sequencer.Trigger.MeridianFlip.MeridianFlipTrigger, OpenAstroAra.Sequencer',
    label: 'Meridian Flip',
    icon: Icons.flip_camera_android_outlined,
  ),
  // Re-connect a device if it drops mid-run. SelectedDevice is one of the
  // ConnectEquipment device names; default "Camera" (the C# constructor default).
  TriggerDef(
    type:
        'OpenAstroAra.Sequencer.Trigger.Connect.ReconnectTrigger, OpenAstroAra.Sequencer',
    label: 'Reconnect Device',
    icon: Icons.cable_outlined,
    fields: [
      InstructionField(
        'SelectedDevice',
        'Device',
        InstructionFieldType.stringEnum,
        defaultValue: 'Camera',
        enumValues: reconnectDeviceNames,
      ),
    ],
  ),
  // §38 NINA import — dither every N exposures (the most common trigger in real plans, one per
  // Smart Exposure). AfterExposures is the cadence; the C# default is 1.
  TriggerDef(
    type:
        'OpenAstroAra.Sequencer.Trigger.Guider.DitherAfterExposures, OpenAstroAra.Sequencer',
    label: 'Dither After Exposures',
    icon: Icons.scatter_plot_outlined,
    fields: [
      InstructionField(
        'AfterExposures',
        'After exposures',
        InstructionFieldType.integer,
        defaultValue: 1,
        min: 1,
      ),
    ],
  ),
  // §38 NINA import — run autofocus every N exposures (the autofocus sibling of the dither trigger).
  TriggerDef(
    type: autofocusAfterExposuresType,
    label: 'Autofocus After Exposures',
    icon: Icons.center_focus_strong_outlined,
    fields: [
      InstructionField(
        'AfterExposures',
        'After exposures',
        InstructionFieldType.integer,
        defaultValue: 1,
        min: 1,
      ),
    ],
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
