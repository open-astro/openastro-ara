/// §38 sequence-editor instruction catalog — the palette of instructions a user
/// can drag into the sequence tree. Each [InstructionDef] mirrors a real C#
/// `OpenAstroAra.Sequencer` instruction class so a node [InstructionDef.build]s
/// deserialises and runs on the daemon unchanged.
///
/// Grounded against the server classes under
/// `OpenAstroAra.Sequencer/SequenceItem/<Category>/`: the assembly-qualified
/// `$type`, the serialized (`[JsonProperty]`) field keys, and the constructor
/// defaults. The stored body uses Newtonsoft `$type` tags with **integer**
/// enums (the daemon's own templates show `"ErrorBehavior": 0`), so enum
/// defaults here are ints — not the string names the REST DTOs use.
///
/// Only instructions that exist as real classes are listed; an unknown `$type`
/// would deserialise to a skipped `Unknown*` placeholder on the daemon, so
/// not-yet-ported instructions (Run Autofocus, Center) are intentionally absent
/// (tracked in design/PORT_TODO.md) rather than faked.
library;

import 'package:flutter/material.dart';

/// The palette groups instructions by the equipment/concern they drive.
enum InstructionCategory { camera, filterWheel, focuser, telescope, guider, utility }

/// Display title for a category section header.
extension InstructionCategoryLabel on InstructionCategory {
  String get label => switch (this) {
        InstructionCategory.camera => 'Camera',
        InstructionCategory.filterWheel => 'Filter Wheel',
        InstructionCategory.focuser => 'Focuser',
        InstructionCategory.telescope => 'Telescope',
        InstructionCategory.guider => 'Guider',
        InstructionCategory.utility => 'Utility',
      };
}

/// The editor control an [InstructionField] renders as, and how its default is
/// shaped in the raw body.
enum InstructionFieldType {
  /// A `double` (e.g. exposure seconds, temperature).
  number,

  /// An `int` (e.g. focuser position, gain).
  integer,

  /// A free-text `string` (e.g. an annotation).
  text,

  /// A `bool` toggle.
  boolean,

  /// An integer-backed enum — [InstructionField.enumLabels] maps value → label.
  intEnum,

  /// A string-valued choice — [InstructionField.enumValues] lists the options.
  stringEnum,

  /// A `BinningMode` object (`{$type, X, Y}`).
  binning,

  /// An `InputCoordinates` object (decomposed RA/Dec).
  coordinates,

  /// A `FilterInfo` object, resolved from the connected filter wheel (nullable
  /// until the user picks one in the editor).
  filter,
}

/// The `BinningMode` default (1×1) the daemon's templates use.
const Map<String, dynamic> defaultBinning = {
  r'$type': 'OpenAstroAra.Core.Model.Equipment.BinningMode, OpenAstroAra.Core',
  'X': 1,
  'Y': 1,
};

/// A zeroed `InputCoordinates` default (RA 00:00:00, Dec +00:00:00).
const Map<String, dynamic> defaultCoordinates = {
  r'$type': 'OpenAstroAra.Astrometry.InputCoordinates, OpenAstroAra.Astrometry',
  'RAHours': 0,
  'RAMinutes': 0,
  'RASeconds': 0.0,
  'NegativeDec': false,
  'DecDegrees': 0,
  'DecMinutes': 0,
  'DecSeconds': 0.0,
};

/// `TrackingMode` enum (Equipment/Interfaces/ITelescope.cs) — integer order.
const Map<int, String> trackingModeLabels = {
  0: 'Sidereal',
  1: 'Lunar',
  2: 'Solar',
  3: 'King',
  4: 'Custom',
  5: 'Stopped',
};

/// The recognised `ImageType` values (TakeExposure).
const List<String> imageTypes = ['LIGHT', 'FLAT', 'DARK', 'BIAS', 'DARKFLAT', 'SNAPSHOT'];

/// One serialized, editable field on an instruction node.
@immutable
class InstructionField {
  /// The raw JSON key (must match the C# `[JsonProperty]` name exactly).
  final String key;

  /// The label shown in the field editor.
  final String label;

  /// How the value is rendered/shaped.
  final InstructionFieldType type;

  /// The constructor default, in raw-body form. Deep-cloned by
  /// [InstructionDef.build] so built nodes never share nested objects.
  final Object? defaultValue;

  /// For [InstructionFieldType.intEnum]: integer value → display label.
  final Map<int, String>? enumLabels;

  /// For [InstructionFieldType.stringEnum]: the allowed string values.
  final List<String>? enumValues;

  /// Whether the user edits this field. Runtime-managed fields (e.g. a running
  /// `ExposureCount`) are emitted with their default but hidden from the editor.
  final bool editable;

  /// Whether the field's default is a placeholder the user MUST replace before
  /// the node is runnable (e.g. `SwitchFilter.Filter` defaults to `null` and is
  /// resolved from the connected wheel). The Phase 3 save path gates on this so
  /// an un-filled instruction can't be saved into a sequence the daemon would
  /// reject or silently skip.
  final bool requiresUserInput;

  const InstructionField(
    this.key,
    this.label,
    this.type, {
    this.defaultValue,
    this.enumLabels,
    this.enumValues,
    this.editable = true,
    this.requiresUserInput = false,
  });
}

/// One draggable instruction in the palette.
@immutable
class InstructionDef {
  /// The assembly-qualified Newtonsoft `$type`.
  final String type;

  /// Palette/title label (e.g. "Take Exposure").
  final String label;

  /// Which palette section it belongs to.
  final InstructionCategory category;

  /// The palette/tree icon.
  final IconData icon;

  /// The editable + runtime-managed fields, in display order.
  final List<InstructionField> fields;

  const InstructionDef({
    required this.type,
    required this.label,
    required this.category,
    required this.icon,
    this.fields = const [],
  });

  /// A fresh raw-body node for this instruction: `$type`, each field at its
  /// (deep-cloned) default, plus the base-item fields every `SequenceItem`
  /// carries (`Parent`/`ErrorBehavior`/`Attempts`) in the shape the daemon's
  /// templates use. The result is fully mutable and shares nothing with the
  /// catalog, so the editor can mutate it via `nina_dom` freely.
  ///
  /// `Name`/`Description` are deliberately NOT emitted: the base `SequenceItem`
  /// exposes them but instruction nodes in the daemon's own templates omit them
  /// (a template `TakeExposure` is exactly `$type` + its fields + `Parent` +
  /// `ErrorBehavior` + `Attempts`), so adding them would diverge from the
  /// runnable shape. Only containers carry `Name`.
  Map<String, dynamic> build() {
    assert(
      fields.map((f) => f.key).toSet().length == fields.length,
      'InstructionDef($label) has duplicate field keys — one would silently '
      'overwrite the other in the built node.',
    );
    final node = <String, dynamic>{r'$type': type};
    for (final f in fields) {
      node[f.key] = _deepCloneJson(f.defaultValue);
    }
    node['Parent'] = null;
    node['ErrorBehavior'] = 0;
    node['Attempts'] = 1;
    return node;
  }
}

/// Recursively copy a JSON value so a built node shares no mutable sub-object
/// (maps/lists) with the const catalog defaults. Scalars and `null` are
/// immutable and returned as-is.
Object? _deepCloneJson(Object? value) {
  if (value is Map) {
    // JSON object keys are strings by construction (these defaults are body
    // fragments). The debug assert makes that invariant self-catching: a future
    // non-`Map<String, dynamic>` default trips here rather than at the `as
    // String` cast below.
    assert(
      value is Map<String, dynamic>,
      'instruction default maps must be String-keyed JSON fragments; got ${value.runtimeType}',
    );
    return <String, dynamic>{
      for (final entry in value.entries) entry.key as String: _deepCloneJson(entry.value),
    };
  }
  if (value is List) {
    return <dynamic>[for (final element in value) _deepCloneJson(element)];
  }
  return value;
}

/// The full instruction palette, in display order within each category.
const List<InstructionDef> instructionCatalog = [
  // ── Camera ────────────────────────────────────────────────────────────────
  InstructionDef(
    type: 'OpenAstroAra.Sequencer.SequenceItem.Imaging.TakeExposure, OpenAstroAra.Sequencer',
    label: 'Take Exposure',
    category: InstructionCategory.camera,
    icon: Icons.camera_alt_outlined,
    fields: [
      InstructionField('ExposureTime', 'Exposure (s)', InstructionFieldType.number, defaultValue: 1.0),
      InstructionField('Gain', 'Gain', InstructionFieldType.integer, defaultValue: -1),
      InstructionField('Offset', 'Offset', InstructionFieldType.integer, defaultValue: -1),
      InstructionField('Binning', 'Binning', InstructionFieldType.binning, defaultValue: defaultBinning),
      InstructionField('ImageType', 'Image type', InstructionFieldType.stringEnum,
          defaultValue: 'LIGHT', enumValues: imageTypes),
      InstructionField('ExposureCount', 'Exposure count', InstructionFieldType.integer,
          defaultValue: 0, editable: false),
    ],
  ),
  InstructionDef(
    type: 'OpenAstroAra.Sequencer.SequenceItem.Camera.CoolCamera, OpenAstroAra.Sequencer',
    label: 'Cool Camera',
    category: InstructionCategory.camera,
    icon: Icons.ac_unit_outlined,
    fields: [
      InstructionField('Temperature', 'Target (°C)', InstructionFieldType.number, defaultValue: 0.0),
      InstructionField('Duration', 'Duration (min)', InstructionFieldType.number, defaultValue: 0.0),
    ],
  ),
  InstructionDef(
    type: 'OpenAstroAra.Sequencer.SequenceItem.Camera.WarmCamera, OpenAstroAra.Sequencer',
    label: 'Warm Camera',
    category: InstructionCategory.camera,
    icon: Icons.wb_sunny_outlined,
    fields: [
      InstructionField('Duration', 'Duration (min)', InstructionFieldType.number, defaultValue: 0.0),
    ],
  ),
  // ── Filter Wheel ────────────────────────────────────────────────────────────
  InstructionDef(
    type: 'OpenAstroAra.Sequencer.SequenceItem.FilterWheel.SwitchFilter, OpenAstroAra.Sequencer',
    label: 'Switch Filter',
    category: InstructionCategory.filterWheel,
    icon: Icons.filter_b_and_w_outlined,
    fields: [
      // FilterInfo, resolved from the connected wheel in the editor (Phase 2);
      // `null` is a placeholder the user must replace before save (Phase 3).
      InstructionField('Filter', 'Filter', InstructionFieldType.filter,
          defaultValue: null, requiresUserInput: true),
    ],
  ),
  // ── Focuser ────────────────────────────────────────────────────────────────
  InstructionDef(
    type: 'OpenAstroAra.Sequencer.SequenceItem.Focuser.MoveFocuserAbsolute, OpenAstroAra.Sequencer',
    label: 'Move Focuser (absolute)',
    category: InstructionCategory.focuser,
    icon: Icons.center_focus_strong_outlined,
    fields: [
      InstructionField('Position', 'Position', InstructionFieldType.integer, defaultValue: 0),
    ],
  ),
  InstructionDef(
    type: 'OpenAstroAra.Sequencer.SequenceItem.Focuser.MoveFocuserRelative, OpenAstroAra.Sequencer',
    label: 'Move Focuser (relative)',
    category: InstructionCategory.focuser,
    icon: Icons.center_focus_weak_outlined,
    fields: [
      InstructionField('RelativePosition', 'Steps', InstructionFieldType.integer, defaultValue: 0),
    ],
  ),
  // ── Telescope ──────────────────────────────────────────────────────────────
  InstructionDef(
    type: 'OpenAstroAra.Sequencer.SequenceItem.Telescope.SlewScopeToRaDec, OpenAstroAra.Sequencer',
    label: 'Slew to RA/Dec',
    category: InstructionCategory.telescope,
    icon: Icons.my_location_outlined,
    fields: [
      InstructionField('Coordinates', 'Coordinates', InstructionFieldType.coordinates,
          defaultValue: defaultCoordinates),
      InstructionField('Inherited', 'Inherit from target', InstructionFieldType.boolean, defaultValue: false),
    ],
  ),
  InstructionDef(
    type: 'OpenAstroAra.Sequencer.SequenceItem.Telescope.SetTracking, OpenAstroAra.Sequencer',
    label: 'Set Tracking',
    category: InstructionCategory.telescope,
    icon: Icons.track_changes_outlined,
    fields: [
      InstructionField('TrackingMode', 'Mode', InstructionFieldType.intEnum,
          defaultValue: 0, enumLabels: trackingModeLabels),
    ],
  ),
  InstructionDef(
    type: 'OpenAstroAra.Sequencer.SequenceItem.Telescope.ParkScope, OpenAstroAra.Sequencer',
    label: 'Park Scope',
    category: InstructionCategory.telescope,
    icon: Icons.local_parking_outlined,
  ),
  InstructionDef(
    type: 'OpenAstroAra.Sequencer.SequenceItem.Telescope.UnparkScope, OpenAstroAra.Sequencer',
    label: 'Unpark Scope',
    category: InstructionCategory.telescope,
    icon: Icons.exit_to_app_outlined,
  ),
  // ── Guider ─────────────────────────────────────────────────────────────────
  InstructionDef(
    type: 'OpenAstroAra.Sequencer.SequenceItem.Guider.StartGuiding, OpenAstroAra.Sequencer',
    label: 'Start Guiding',
    category: InstructionCategory.guider,
    icon: Icons.play_circle_outline,
    fields: [
      InstructionField('ForceCalibration', 'Force calibration', InstructionFieldType.boolean,
          defaultValue: false),
    ],
  ),
  InstructionDef(
    type: 'OpenAstroAra.Sequencer.SequenceItem.Guider.StopGuiding, OpenAstroAra.Sequencer',
    label: 'Stop Guiding',
    category: InstructionCategory.guider,
    icon: Icons.stop_circle_outlined,
  ),
  // Dither carries no serialized fields of its own (its C# class declares no
  // `[JsonProperty]`); the "after N exposures" / settle parameters live on the
  // dither *trigger*, not this instruction.
  InstructionDef(
    type: 'OpenAstroAra.Sequencer.SequenceItem.Guider.Dither, OpenAstroAra.Sequencer',
    label: 'Dither',
    category: InstructionCategory.guider,
    icon: Icons.scatter_plot_outlined,
  ),
  // ── Utility ────────────────────────────────────────────────────────────────
  InstructionDef(
    type: 'OpenAstroAra.Sequencer.SequenceItem.Utility.WaitForTimeSpan, OpenAstroAra.Sequencer',
    label: 'Wait (duration)',
    category: InstructionCategory.utility,
    icon: Icons.hourglass_empty_outlined,
    fields: [
      // `Time` is a plain `double` of seconds in the C# class (not a
      // TimeSpan-serialized string), so a bare number is the correct shape.
      InstructionField('Time', 'Seconds', InstructionFieldType.number, defaultValue: 1.0),
    ],
  ),
  InstructionDef(
    type: 'OpenAstroAra.Sequencer.SequenceItem.Utility.WaitForTime, OpenAstroAra.Sequencer',
    label: 'Wait (until time)',
    category: InstructionCategory.utility,
    icon: Icons.schedule_outlined,
    // All four are `[JsonProperty] public int` in the C# WaitForTime class
    // (not double/TimeSpan), so `integer` fields match the on-disk shape.
    fields: [
      InstructionField('Hours', 'Hour', InstructionFieldType.integer, defaultValue: 0),
      InstructionField('Minutes', 'Minute', InstructionFieldType.integer, defaultValue: 0),
      InstructionField('Seconds', 'Second', InstructionFieldType.integer, defaultValue: 0),
      InstructionField('MinutesOffset', 'Offset (min)', InstructionFieldType.integer, defaultValue: 0),
    ],
  ),
  InstructionDef(
    type: 'OpenAstroAra.Sequencer.SequenceItem.Utility.Annotation, OpenAstroAra.Sequencer',
    label: 'Annotation',
    category: InstructionCategory.utility,
    icon: Icons.sticky_note_2_outlined,
    fields: [
      InstructionField('Text', 'Note', InstructionFieldType.text, defaultValue: ''),
    ],
  ),
];

/// The catalog grouped by category, preserving declaration order, with empty
/// categories omitted. Built once for the palette UI. Both the outer map and
/// each inner list are unmodifiable so the singleton can't be corrupted.
final Map<InstructionCategory, List<InstructionDef>> instructionCatalogByCategory = () {
  final grouped = <InstructionCategory, List<InstructionDef>>{};
  for (final def in instructionCatalog) {
    (grouped[def.category] ??= <InstructionDef>[]).add(def);
  }
  return Map<InstructionCategory, List<InstructionDef>>.unmodifiable(
    grouped.map((k, v) => MapEntry(k, List<InstructionDef>.unmodifiable(v))),
  );
}();

/// `$type` → [InstructionDef] index, so [instructionForType] is O(1) even if
/// called per render frame in the palette/tree.
final Map<String, InstructionDef> _instructionsByType = Map.unmodifiable(
  {for (final def in instructionCatalog) def.type: def},
);

/// The [InstructionDef] whose `$type` equals [type], or null if not catalogued
/// (e.g. an instruction the editor can display but not yet construct).
InstructionDef? instructionForType(String type) => _instructionsByType[type];
