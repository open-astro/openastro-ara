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

import 'nina_dom.dart' show conditionsWrapperType, itemsWrapperType, triggersWrapperType;

/// The palette groups instructions by the equipment/concern they drive.
/// `container` holds the nesting building-blocks (sequential/parallel sets).
enum InstructionCategory { camera, filterWheel, focuser, telescope, guider, utility, container }

/// Display title for a category section header.
extension InstructionCategoryLabel on InstructionCategory {
  String get label => switch (this) {
        InstructionCategory.camera => 'Camera',
        InstructionCategory.filterWheel => 'Filter Wheel',
        InstructionCategory.focuser => 'Focuser',
        InstructionCategory.telescope => 'Telescope',
        InstructionCategory.guider => 'Guider',
        InstructionCategory.utility => 'Utility',
        InstructionCategory.container => 'Containers',
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

  /// A nested `WaitLoopData` object (the altitude conditions' `Data`): an
  /// `InputCoordinates` target, a degrees `Offset`, and a `Comparator`. Rendered
  /// as a composite sub-editor that rebuilds the whole object on any change.
  waitLoopData,

  /// A `TimeCondition.SelectedProvider` — null (an absolute clock time) or a
  /// sky-event provider object (`{$type}`, e.g. civil dusk). Rendered as a
  /// dropdown over the fixed `timeProviders` map.
  timeProvider,
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

  /// Optional inclusive bounds for a `number`/`integer` field. When either is
  /// set, the editor renders a clamped controlled field (the entry snaps into
  /// range and the displayed text is corrected) instead of a free text field —
  /// so e.g. a `TimeSpanCondition`'s minutes can't be set above 59 or negative.
  final num? min;
  final num? max;

  /// Optional relevance predicate over the field's **sibling node** (the
  /// instruction/condition/trigger map this field belongs to). When it returns
  /// false the editor greys out and disables the field because the other fields'
  /// values make it inert — e.g. a `TimeCondition`'s H/M/S are ignored by the
  /// daemon (which computes the time) once a sky-event `SelectedProvider` is
  /// chosen. Null = always enabled. Must be a const-constructible reference (a
  /// top-level/static function), since the catalogs are `const`.
  final bool Function(Map<String, dynamic> node)? enabledWhen;

  const InstructionField(
    this.key,
    this.label,
    this.type, {
    this.defaultValue,
    this.enumLabels,
    this.enumValues,
    this.editable = true,
    this.requiresUserInput = false,
    this.min,
    this.max,
    this.enabledWhen,
  })  : assert(
          enumLabels == null || enumValues == null,
          'a field is either an intEnum (enumLabels) or a stringEnum (enumValues), '
          'never both — setting both is an ambiguous authoring mistake.',
        ),
        assert(type != InstructionFieldType.intEnum || enumLabels != null,
            'an intEnum field must provide enumLabels'),
        assert(type != InstructionFieldType.stringEnum || enumValues != null,
            'a stringEnum field must provide enumValues'),
        assert(min == null || max == null || min <= max,
            'a field\'s min must be <= its max'),
        assert((min == null && max == null) ||
            type == InstructionFieldType.number ||
            type == InstructionFieldType.integer,
            'min/max only apply to a number or integer field'),
        // The numeric text formatters reject "-", so a negative min would trap
        // the user at 0. Tombstone until the formatters allow a leading minus.
        assert(min == null || min >= 0,
            'negative min unsupported — the numeric field formatters reject "-"'),
        // A const default must already be in range (the field only snaps on the
        // first edit, so an out-of-range default would open invalid).
        assert(defaultValue is! num || min == null || defaultValue >= min,
            'defaultValue must be >= min'),
        assert(defaultValue is! num || max == null || defaultValue <= max,
            'defaultValue must be <= max');
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

  /// For a container, the assembly-qualified `$type` of its execution
  /// `Strategy` (Sequential/Parallel). Non-null marks this def a container:
  /// [build] emits the full container shape (a typed `Strategy`, a `Name`, and
  /// empty `Conditions`/`Items`/`Triggers` collections) instead of a leaf node.
  final String? strategyType;

  /// For a container, the default `Name` a freshly-built node carries — leaf
  /// instructions never emit `Name`. Defaults to [label] when omitted.
  final String? defaultName;

  /// Whether this def builds a container (a node that holds child items) rather
  /// than a leaf instruction. Distinct from `nina_dom`'s `isContainer(node)`,
  /// which inspects a built node; this inspects the catalog def.
  bool get isContainer => strategyType != null;

  const InstructionDef({
    required this.type,
    required this.label,
    required this.category,
    required this.icon,
    this.fields = const [],
    this.strategyType,
    this.defaultName,
  }) : assert(strategyType != null || defaultName == null,
            'defaultName only applies to a container (set strategyType too)');

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
    // A real throw (not a debug assert): a duplicate key would silently
    // overwrite in the node, so reject it in release too — this guards a
    // dynamically-constructed InstructionDef (e.g. a Phase 2 generator), not
    // just the const catalog the test already checks.
    final keys = fields.map((f) => f.key).toSet();
    if (keys.length != fields.length) {
      throw StateError('InstructionDef($label) has duplicate field keys');
    }
    // The const-ctor assert catches this for the static catalog at construction;
    // re-check in release too (matching the duplicate-key guard above) so a
    // dynamically-built def with a defaultName but no strategyType — whose name
    // _buildContainer would otherwise silently drop on a leaf — fails fast.
    if (strategyType == null && defaultName != null) {
      throw StateError(
          'InstructionDef($label) sets defaultName without strategyType (only containers carry a Name)');
    }
    if (isContainer) return _buildContainer();
    // A field may not reuse a base key build() writes itself (it would be
    // silently clobbered). Shared with the condition/trigger catalogs.
    checkNoReservedFieldKeys('InstructionDef($label)', fields,
        const {r'$type', 'Parent', 'ErrorBehavior', 'Attempts'});
    final node = <String, dynamic>{r'$type': type};
    for (final f in fields) {
      node[f.key] = deepCloneJson(f.defaultValue);
    }
    node['Parent'] = null;
    node['ErrorBehavior'] = 0;
    node['Attempts'] = 1;
    return node;
  }

  /// A fresh, empty container node in the daemon's template shape: a typed
  /// `Strategy`, a user-facing `Name`, empty `Conditions`/`Items`/`Triggers`
  /// ObservableCollections, plus the base-item fields. Field order mirrors the
  /// daemon's own templates (Strategy → Name → Conditions → IsExpanded → Items
  /// → Triggers → base). The three collections are fresh growable lists, so the
  /// editor can nest children into a just-added container immediately via
  /// `nina_dom` (which appends to `Items.$values`).
  Map<String, dynamic> _buildContainer() => <String, dynamic>{
        r'$type': type,
        'Strategy': <String, dynamic>{r'$type': strategyType},
        // The two current containers reuse their palette label as the NINA
        // default Name. If a future container's NINA default name diverges from
        // its palette label, set `defaultName` explicitly so the right name
        // round-trips into NINA.
        'Name': defaultName ?? label,
        'Conditions': <String, dynamic>{
          r'$type': conditionsWrapperType,
          r'$values': <dynamic>[],
        },
        'IsExpanded': true,
        'Items': <String, dynamic>{
          r'$type': itemsWrapperType,
          r'$values': <dynamic>[],
        },
        'Triggers': <String, dynamic>{
          r'$type': triggersWrapperType,
          r'$values': <dynamic>[],
        },
        'Parent': null,
        'ErrorBehavior': 0,
        'Attempts': 1,
      };
}

/// Throws a [StateError] if any of [fields] uses a key in [reserved] — a base
/// key the caller's `build()` writes itself, which a field would silently
/// clobber. Shared by the instruction / condition / trigger catalogs so all
/// three guard their reserved base keys identically. [owner] labels the throw.
void checkNoReservedFieldKeys(
    String owner, List<InstructionField> fields, Set<String> reserved) {
  for (final f in fields) {
    if (reserved.contains(f.key)) {
      throw StateError('$owner field "${f.key}" collides with a reserved base key');
    }
  }
}

/// Recursively copy a JSON value so a built node shares no mutable sub-object
/// (maps/lists) with the const catalog defaults. Scalars and `null` are
/// immutable and returned as-is. Shared by [ConditionDef.build] (condition
/// catalog) so both build paths clone defaults the same way.
Object? deepCloneJson(Object? value) {
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
      for (final entry in value.entries) entry.key as String: deepCloneJson(entry.value),
    };
  }
  if (value is List) {
    return <dynamic>[for (final element in value) deepCloneJson(element)];
  }
  return value;
}

/// The full instruction palette, in display order within each category.
/// The assembly-qualified `$type` of the general-purpose sequential container —
/// the single source of truth for this string (also reused by the trigger
/// catalog for a trigger's `TriggerRunner`).
const String sequentialContainerType =
    'OpenAstroAra.Sequencer.Container.SequentialContainer, OpenAstroAra.Sequencer';

/// The assembly-qualified `$type` of the "Slew to RA/Dec" telescope instruction
/// — the single source of truth, reused by the Planning tab's "Add to sequence"
/// body builder so a typo can't drift the two apart.
const String slewScopeToRaDecType =
    'OpenAstroAra.Sequencer.SequenceItem.Telescope.SlewScopeToRaDec, OpenAstroAra.Sequencer';

const List<InstructionDef> instructionCatalog = [
  // ── Camera ────────────────────────────────────────────────────────────────
  InstructionDef(
    type: 'OpenAstroAra.Sequencer.SequenceItem.Imaging.TakeExposure, OpenAstroAra.Sequencer',
    label: 'Take Exposure',
    category: InstructionCategory.camera,
    icon: Icons.camera_alt_outlined,
    fields: [
      InstructionField('ExposureTime', 'Exposure (s)', InstructionFieldType.number, defaultValue: 1.0),
      // -1 is the NINA sentinel for "use the camera's profile default" (not 0).
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
      // A 5-minute ramp by default — a 0-min duration tells the daemon to jump
      // straight to the target, which thermally shocks the sensor.
      InstructionField('Duration', 'Duration (min)', InstructionFieldType.number, defaultValue: 5.0),
    ],
  ),
  InstructionDef(
    type: 'OpenAstroAra.Sequencer.SequenceItem.Camera.WarmCamera, OpenAstroAra.Sequencer',
    label: 'Warm Camera',
    category: InstructionCategory.camera,
    icon: Icons.wb_sunny_outlined,
    fields: [
      // 5-minute ramp by default (gentle warm-up), as with Cool Camera.
      InstructionField('Duration', 'Duration (min)', InstructionFieldType.number, defaultValue: 5.0),
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
  // §38 NINA import — run an autofocus routine. No serialized fields.
  InstructionDef(
    type: 'OpenAstroAra.Sequencer.SequenceItem.Autofocus.RunAutofocus, OpenAstroAra.Sequencer',
    label: 'Run Autofocus',
    category: InstructionCategory.focuser,
    icon: Icons.center_focus_strong_outlined,
  ),
  // ── Telescope ──────────────────────────────────────────────────────────────
  InstructionDef(
    type: slewScopeToRaDecType,
    label: 'Slew to RA/Dec',
    category: InstructionCategory.telescope,
    icon: Icons.my_location_outlined,
    fields: [
      InstructionField('Coordinates', 'Coordinates', InstructionFieldType.coordinates,
          defaultValue: defaultCoordinates),
      InstructionField('Inherited', 'Inherit from target', InstructionFieldType.boolean, defaultValue: false),
    ],
  ),
  // §38 NINA import — center-and-rotate: slew + plate-solve to centre, then rotate to PositionAngle.
  InstructionDef(
    type: 'OpenAstroAra.Sequencer.SequenceItem.Platesolving.CenterAndRotate, OpenAstroAra.Sequencer',
    label: 'Center and Rotate',
    category: InstructionCategory.telescope,
    icon: Icons.crop_rotate_outlined,
    fields: [
      InstructionField('Coordinates', 'Coordinates', InstructionFieldType.coordinates,
          defaultValue: defaultCoordinates),
      InstructionField('PositionAngle', 'Position angle (°)', InstructionFieldType.number, defaultValue: 0.0),
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
  // ── Containers ───────────────────────────────────────────────────────────
  // The nesting building-blocks. A container holds child Items (and, in later
  // slices, Conditions/Triggers). NINA's default container names are kept so an
  // ARA-built sequence reads identically when opened in NINA. Appended last so
  // the palette's existing instruction rows keep their positions.
  InstructionDef(
    type: sequentialContainerType,
    label: 'Sequential Instruction Set',
    category: InstructionCategory.container,
    icon: Icons.account_tree_outlined,
    strategyType:
        'OpenAstroAra.Sequencer.Container.ExecutionStrategy.SequentialStrategy, OpenAstroAra.Sequencer',
  ),
  InstructionDef(
    type: 'OpenAstroAra.Sequencer.Container.ParallelContainer, OpenAstroAra.Sequencer',
    label: 'Parallel Instruction Set',
    category: InstructionCategory.container,
    icon: Icons.call_split,
    strategyType:
        'OpenAstroAra.Sequencer.Container.ExecutionStrategy.ParallelStrategy, OpenAstroAra.Sequencer',
  ),
  // §38 NINA import — the per-target block. A SequentialContainer subclass that also carries a
  // Target (name + coordinates); the target name is surfaced in the tree label (see node_display).
  InstructionDef(
    type:
        'OpenAstroAra.Sequencer.Container.DeepSkyObjectContainer, OpenAstroAra.Sequencer',
    label: 'Deep Sky Object',
    category: InstructionCategory.container,
    icon: Icons.travel_explore_outlined,
    strategyType:
        'OpenAstroAra.Sequencer.Container.ExecutionStrategy.SequentialStrategy, OpenAstroAra.Sequencer',
  ),
  // §38 NINA import — NINA's "Smart Exposure": a SequentialContainer subclass bundling a filter
  // switch + exposure + dither into one block. Renders as a container; its children show nested.
  InstructionDef(
    type:
        'OpenAstroAra.Sequencer.SequenceItem.Imaging.SmartExposure, OpenAstroAra.Sequencer',
    label: 'Smart Exposure',
    category: InstructionCategory.container,
    icon: Icons.burst_mode_outlined,
    strategyType:
        'OpenAstroAra.Sequencer.Container.ExecutionStrategy.SequentialStrategy, OpenAstroAra.Sequencer',
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
/// called per render frame in the palette/tree. Throws (in release too) on a
/// duplicate `$type` rather than last-wins-shadowing it — fail-fast for a future
/// dynamically-built catalog, matching [InstructionDef.build]'s key guard.
final Map<String, InstructionDef> _instructionsByType = () {
  final byType = <String, InstructionDef>{};
  for (final def in instructionCatalog) {
    if (byType.containsKey(def.type)) {
      throw StateError('duplicate InstructionDef \$type: ${def.type}');
    }
    byType[def.type] = def;
  }
  return Map<String, InstructionDef>.unmodifiable(byType);
}();

/// The [InstructionDef] whose `$type` equals [type], or null if not catalogued
/// (e.g. an instruction the editor can display but not yet construct).
InstructionDef? instructionForType(String type) => _instructionsByType[type];
