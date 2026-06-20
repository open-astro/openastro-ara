/// §38 sequence-editor field panel — edits the scalar fields of the node
/// currently selected in the tree. Each control is driven by the instruction
/// catalog's [InstructionField] schema and writes straight back to the RAW body
/// via [SequenceEditorController.setNodeField]. Binning has an inline `X × Y`
/// editor, coordinates an RA/Dec editor, and the filter field a picker over the
/// configured filter-wheel slot labels.
///
/// For a selected container, the panel also edits its `Name`, its loop
/// **Conditions**, and its **Triggers** (add/remove/edit) via the controller's
/// condition / trigger ops.
library;

import 'package:flutter/material.dart';
import 'package:flutter/services.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../models/sequence/condition_catalog.dart';
import '../../models/sequence/instruction_catalog.dart';
import '../../models/sequence/nina_dom.dart';
import '../../models/sequence/node_display.dart' show nodeLabel, shortTypeName;
import '../../models/sequence/trigger_catalog.dart';
import '../../state/settings/filter_wheel_labels_state.dart';
import '../../state/sequencer/sequence_editor_state.dart';
import '../../theme/ara_colors.dart';

class SequenceFieldEditor extends ConsumerWidget {
  const SequenceFieldEditor({super.key});

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final editor = ref.watch(sequenceEditorProvider);
    final path = editor?.selectedPath;
    final node = (editor != null && path != null) ? nodeAt(editor.body, path) : null;

    if (node == null || path == null) {
      return const _Placeholder('Select an instruction to edit its settings.');
    }
    final selectedPath = path; // promoted non-null for the closures below

    final type = node[r'$type'];
    final def = type is String ? instructionForType(type) : null;
    final editable = def?.fields.where((f) => f.editable).toList() ?? const [];
    // Containers are user-named; their Name is edited here (leaves never carry
    // one). isContainer recognises both catalogued and imported container types.
    final container = isContainer(node);
    final notifier = ref.read(sequenceEditorProvider.notifier);

    final children = <Widget>[
      Text(
        nodeLabel(node),
        style: const TextStyle(
            color: AraColors.textPrimary, fontSize: 14, fontWeight: FontWeight.w600),
      ),
      const SizedBox(height: 12),
      if (container) ...[
        _NameEditor(
          // Fresh control when the selection changes (re-seeds initialValue).
          key: ValueKey('${selectedPath.join(".")}/__name'),
          name: node['Name'] is String ? node['Name'] as String : '',
          onChanged: (v) => notifier.setNodeField(selectedPath, 'Name', v),
        ),
        const SizedBox(height: 16),
        _ConditionsSection(
          containerPath: selectedPath,
          conditions: conditionsOf(node),
        ),
        const SizedBox(height: 16),
        _TriggersSection(
          containerPath: selectedPath,
          triggers: triggersOf(node),
        ),
        const SizedBox(height: 12),
      ],
      for (final field in editable)
        Padding(
          padding: const EdgeInsets.only(bottom: 12),
          child: _FieldControl(
            // Fresh control when the selection or field changes.
            key: ValueKey('${selectedPath.join(".")}/${field.key}'),
            field: field,
            value: node[field.key],
            onChanged: (v) => notifier.setNodeField(selectedPath, field.key, v),
          ),
        ),
    ];

    // A placeholder only when there's nothing else to show — never for a
    // container (its Name editor is the setting).
    if (editable.isEmpty && !container) {
      children.add(_Placeholder(def == null
          ? 'This instruction has no editable fields here.'
          : 'No settings — this instruction runs as-is.'));
    }

    return ListView(padding: const EdgeInsets.all(12), children: children);
  }
}

/// A container's `Name` editor — a labelled free-text field. Commits every
/// keystroke straight to the raw body's `Name` (an empty name is allowed — the
/// tree falls back to the catalog label for it). Like [_NumField], it owns a
/// controller and re-syncs in [didUpdateWidget] when [name] changes externally
/// (e.g. a future undo/redo) so the displayed text can't lag the model. The
/// path-based [Key] in the parent still gives a fresh editor per selection.
class _NameEditor extends StatefulWidget {
  const _NameEditor({super.key, required this.name, required this.onChanged});

  final String name;
  final ValueChanged<String> onChanged;

  @override
  State<_NameEditor> createState() => _NameEditorState();
}

class _NameEditorState extends State<_NameEditor> {
  late final TextEditingController _controller =
      TextEditingController(text: widget.name);

  @override
  void didUpdateWidget(_NameEditor old) {
    super.didUpdateWidget(old);
    // Re-seed only on a genuine external change, and never clobber an identical
    // value mid-edit (would move the caret): the guard on _controller.text keeps
    // the user's own keystroke-driven updates untouched.
    if (widget.name != old.name && _controller.text != widget.name) {
      _controller.text = widget.name;
    }
  }

  @override
  void dispose() {
    _controller.dispose();
    super.dispose();
  }

  @override
  Widget build(BuildContext context) => Column(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          const Text('Name',
              style: TextStyle(color: AraColors.textSecondary, fontSize: 12)),
          const SizedBox(height: 4),
          TextField(
            key: const Key('container_name'),
            controller: _controller,
            style: const TextStyle(color: AraColors.textPrimary, fontSize: 13),
            decoration:
                const InputDecoration(isDense: true, border: OutlineInputBorder()),
            onChanged: widget.onChanged,
          ),
        ],
      );
}

/// A container's loop-**Conditions** editor: a header with an "add" menu, then a
/// card per condition (its catalogued fields + a remove button). Edits route to
/// the controller's condition ops, addressed by `(containerPath, index)`. Shown
/// only for a selected container (a leaf carries no conditions).
class _ConditionsSection extends ConsumerWidget {
  const _ConditionsSection({required this.containerPath, required this.conditions});

  final NodePath containerPath;
  final List<Map<String, dynamic>> conditions;

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final notifier = ref.read(sequenceEditorProvider.notifier);
    return Column(
      crossAxisAlignment: CrossAxisAlignment.start,
      children: [
        Row(
          children: [
            const Text('Conditions',
                style: TextStyle(
                    color: AraColors.textPrimary, fontSize: 13, fontWeight: FontWeight.w600)),
            const Spacer(),
            PopupMenuButton<ConditionDef>(
              tooltip: 'Add condition',
              icon: const Icon(Icons.add, size: 20, color: AraColors.textSecondary),
              onSelected: (def) => notifier.addConditionTo(containerPath, def),
              itemBuilder: (_) => [
                for (final def in conditionCatalog)
                  PopupMenuItem<ConditionDef>(
                    value: def,
                    child: Row(children: [
                      Icon(def.icon, size: 18),
                      const SizedBox(width: 8),
                      Flexible(child: Text(def.label, overflow: TextOverflow.ellipsis)),
                    ]),
                  ),
              ],
            ),
          ],
        ),
        if (conditions.isEmpty)
          const Padding(
            padding: EdgeInsets.symmetric(vertical: 4),
            child: Text('No loop conditions — the container runs once.',
                style: TextStyle(color: AraColors.textSecondary, fontSize: 12)),
          )
        else
          for (var i = 0; i < conditions.length; i++)
            _ConditionCard(
              key: ValueKey('${containerPath.join(".")}/cond/$i'),
              containerPath: containerPath,
              index: i,
              condition: conditions[i],
            ),
      ],
    );
  }
}

/// One condition row: its label/icon + a remove button, and an editor per
/// catalogued editable field (writing through `setConditionFieldOn`).
class _ConditionCard extends ConsumerWidget {
  const _ConditionCard({
    super.key,
    required this.containerPath,
    required this.index,
    required this.condition,
  });

  final NodePath containerPath;
  final int index;
  final Map<String, dynamic> condition;

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final notifier = ref.read(sequenceEditorProvider.notifier);
    final type = condition[r'$type'];
    final def = type is String ? conditionForType(type) : null;
    final label = def?.label ?? (type is String ? shortTypeName(type) : null) ?? 'Condition';
    final fields = def?.fields.where((f) => f.editable).toList() ?? const [];

    return Container(
      margin: const EdgeInsets.only(top: 8),
      padding: const EdgeInsets.fromLTRB(10, 6, 6, 10),
      decoration: BoxDecoration(
        border: Border.all(color: AraColors.border),
        borderRadius: BorderRadius.circular(6),
      ),
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          Row(
            children: [
              Icon(def?.icon ?? Icons.rule, size: 16, color: AraColors.textSecondary),
              const SizedBox(width: 6),
              Expanded(
                child: Text(label,
                    style: const TextStyle(color: AraColors.textPrimary, fontSize: 12)),
              ),
              IconButton(
                tooltip: 'Remove condition',
                icon: const Icon(Icons.delete_outline, size: 18),
                visualDensity: VisualDensity.compact,
                onPressed: () => notifier.removeConditionFrom(containerPath, index),
              ),
            ],
          ),
          for (final field in fields)
            Padding(
              padding: const EdgeInsets.only(top: 8),
              child: _FieldControl(
                key: ValueKey('${containerPath.join(".")}/cond/$index/${field.key}'),
                field: field,
                value: condition[field.key],
                onChanged: (v) =>
                    notifier.setConditionFieldOn(containerPath, index, field.key, v),
              ),
            ),
        ],
      ),
    );
  }
}

/// A container's **Triggers** editor (mirrors [_ConditionsSection]): a header
/// with an "add" menu, then a card per trigger. A trigger fires between
/// instructions while the container runs (e.g. the meridian flip).
class _TriggersSection extends ConsumerWidget {
  const _TriggersSection({required this.containerPath, required this.triggers});

  final NodePath containerPath;
  final List<Map<String, dynamic>> triggers;

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final notifier = ref.read(sequenceEditorProvider.notifier);
    return Column(
      crossAxisAlignment: CrossAxisAlignment.start,
      children: [
        Row(
          children: [
            const Text('Triggers',
                style: TextStyle(
                    color: AraColors.textPrimary, fontSize: 13, fontWeight: FontWeight.w600)),
            const Spacer(),
            PopupMenuButton<TriggerDef>(
              tooltip: 'Add trigger',
              icon: const Icon(Icons.add, size: 20, color: AraColors.textSecondary),
              onSelected: (def) => notifier.addTriggerTo(containerPath, def),
              itemBuilder: (_) => [
                for (final def in triggerCatalog)
                  PopupMenuItem<TriggerDef>(
                    value: def,
                    child: Row(children: [
                      Icon(def.icon, size: 18),
                      const SizedBox(width: 8),
                      Flexible(child: Text(def.label, overflow: TextOverflow.ellipsis)),
                    ]),
                  ),
              ],
            ),
          ],
        ),
        if (triggers.isEmpty)
          const Padding(
            padding: EdgeInsets.symmetric(vertical: 4),
            child: Text('No triggers.',
                style: TextStyle(color: AraColors.textSecondary, fontSize: 12)),
          )
        else
          for (var i = 0; i < triggers.length; i++)
            _TriggerCard(
              key: ValueKey('${containerPath.join(".")}/trig/$i'),
              containerPath: containerPath,
              index: i,
              trigger: triggers[i],
            ),
      ],
    );
  }
}

/// One trigger row: its label/icon + a remove button, and an editor per
/// catalogued editable field (writing through `setTriggerFieldOn`). Meridian
/// Flip has no editable fields, so its card is just the label + remove.
class _TriggerCard extends ConsumerWidget {
  const _TriggerCard({
    super.key,
    required this.containerPath,
    required this.index,
    required this.trigger,
  });

  final NodePath containerPath;
  final int index;
  final Map<String, dynamic> trigger;

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final notifier = ref.read(sequenceEditorProvider.notifier);
    final type = trigger[r'$type'];
    final def = type is String ? triggerForType(type) : null;
    final label = def?.label ?? (type is String ? shortTypeName(type) : null) ?? 'Trigger';
    final fields = def?.fields.where((f) => f.editable).toList() ?? const [];

    return Container(
      margin: const EdgeInsets.only(top: 8),
      padding: const EdgeInsets.fromLTRB(10, 6, 6, 10),
      decoration: BoxDecoration(
        border: Border.all(color: AraColors.border),
        borderRadius: BorderRadius.circular(6),
      ),
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          Row(
            children: [
              Icon(def?.icon ?? Icons.bolt_outlined, size: 16, color: AraColors.textSecondary),
              const SizedBox(width: 6),
              Expanded(
                child: Text(label,
                    style: const TextStyle(color: AraColors.textPrimary, fontSize: 12)),
              ),
              IconButton(
                tooltip: 'Remove trigger',
                icon: const Icon(Icons.delete_outline, size: 18),
                visualDensity: VisualDensity.compact,
                onPressed: () => notifier.removeTriggerFrom(containerPath, index),
              ),
            ],
          ),
          for (final field in fields)
            Padding(
              padding: const EdgeInsets.only(top: 8),
              child: _FieldControl(
                key: ValueKey('${containerPath.join(".")}/trig/$index/${field.key}'),
                field: field,
                value: trigger[field.key],
                onChanged: (v) =>
                    notifier.setTriggerFieldOn(containerPath, index, field.key, v),
              ),
            ),
        ],
      ),
    );
  }
}

class _FieldControl extends StatelessWidget {
  const _FieldControl({
    super.key,
    required this.field,
    required this.value,
    required this.onChanged,
  });

  final InstructionField field;
  final Object? value;
  final ValueChanged<Object?> onChanged;

  @override
  Widget build(BuildContext context) {
    switch (field.type) {
      case InstructionFieldType.boolean:
        return Row(
          children: [
            Expanded(child: _label()),
            Switch(
              value: value == true,
              onChanged: onChanged,
            ),
          ],
        );
      case InstructionFieldType.intEnum:
        final labels = field.enumLabels ?? const <int, String>{};
        final intVal = value is int ? value as int : null;
        return _labelled(
          DropdownButton<int>(
            // Null unless the stored value is a known variant, else Flutter
            // asserts "exactly one item with the DropdownButton's value".
            value: labels.containsKey(intVal) ? intVal : null,
            isExpanded: true,
            dropdownColor: AraColors.bgPanel,
            items: [
              for (final e in labels.entries)
                DropdownMenuItem(value: e.key, child: Text(e.value)),
            ],
            onChanged: (v) {
              if (v != null) onChanged(v);
            },
          ),
        );
      case InstructionFieldType.stringEnum:
        final values = field.enumValues ?? const <String>[];
        final strVal = value is String ? value as String : null;
        return _labelled(
          DropdownButton<String>(
            value: values.contains(strVal) ? strVal : null,
            isExpanded: true,
            dropdownColor: AraColors.bgPanel,
            items: [
              for (final v in values)
                DropdownMenuItem(value: v, child: Text(v)),
            ],
            onChanged: (v) {
              if (v != null) onChanged(v);
            },
          ),
        );
      case InstructionFieldType.number:
        if (field.min != null || field.max != null) {
          return _labelled(_NumField(
            // A non-num (missing/corrupt) stored value falls back to the lower
            // bound (or 0) so the control still renders; the model is corrected
            // on the first edit (_NumField clamps + corrects the display).
            value: value is num ? value as num : (field.min ?? 0),
            isInt: false,
            min: field.min,
            max: field.max,
            onChanged: (v) => onChanged(v.toDouble()),
          ));
        }
        return _labelled(_textField(
          initial: value == null ? '' : '$value',
          keyboard: const TextInputType.numberWithOptions(decimal: true, signed: true),
          parse: (s) => double.tryParse(s),
        ));
      case InstructionFieldType.integer:
        if (field.min != null || field.max != null) {
          return _labelled(_NumField(
            // Same defensive fallback as the number case above.
            value: value is num ? value as num : (field.min ?? 0),
            isInt: true,
            min: field.min,
            max: field.max,
            onChanged: (v) => onChanged(v.toInt()),
          ));
        }
        return _labelled(_textField(
          initial: value == null ? '' : '$value',
          keyboard: const TextInputType.numberWithOptions(signed: true),
          parse: (s) => int.tryParse(s),
        ));
      case InstructionFieldType.text:
        return _labelled(_textField(
          initial: value is String ? value as String : '',
          keyboard: TextInputType.text,
          parse: (s) => s,
        ));
      case InstructionFieldType.binning:
        return _labelled(_binningEditor());
      case InstructionFieldType.coordinates:
        return _labelled(_CoordinatesEditor(
          fieldKey: field.key,
          value: value,
          onChanged: onChanged,
        ));
      case InstructionFieldType.waitLoopData:
        return _labelled(_WaitLoopDataEditor(
          fieldKey: field.key,
          value: value,
          // Fall back to THIS condition's own catalogued Comparator default
          // (AltitudeCondition → LessThan, AboveHorizon → GreaterThan) for a body
          // that arrives without one, rather than a fixed GreaterThan bias.
          fallbackComparator: _comparatorOf(field.defaultValue),
          onChanged: onChanged,
        ));
      case InstructionFieldType.filter:
        return _labelled(_FilterEditor(value: value, onChanged: onChanged));
    }
  }

  /// The `Comparator` from a `waitLoopData` field's catalogued default `Data`
  /// map, or 3 (GreaterThan, the daemon-coerced safe value) if absent. Used as
  /// the per-condition fallback when a stored node has no Comparator.
  static int _comparatorOf(Object? dataDefault) {
    final c = dataDefault is Map ? dataDefault['Comparator'] : null;
    return c is num ? c.toInt() : 3;
  }

  /// `X × Y` integer editor over the nested `BinningMode` object. Each change
  /// rebuilds the whole map (preserving its `$type` and the other axis) and
  /// writes it back via [onChanged]. Binning factors must be ≥ 1.
  Widget _binningEditor() {
    final bin = value is Map ? value as Map : const {};
    final x = bin['X'] is int ? bin['X'] as int : 1;
    final y = bin['Y'] is int ? bin['Y'] as int : 1;
    final type = bin[r'$type'] is String ? bin[r'$type'] as String : defaultBinning[r'$type'];
    Map<String, dynamic> withAxis({int? newX, int? newY}) => <String, dynamic>{
          r'$type': type,
          'X': newX ?? x,
          'Y': newY ?? y,
        };
    return Row(
      children: [
        SizedBox(
          width: 56,
          child: _NumField(
            // Scoped to the field so two binning fields couldn't collide.
            key: Key('${field.key}_x'),
            value: x,
            isInt: true,
            min: 1,
            onChanged: (v) => onChanged(withAxis(newX: v.toInt())),
          ),
        ),
        const Padding(
          padding: EdgeInsets.symmetric(horizontal: 8),
          child: Text('×', style: TextStyle(color: AraColors.textSecondary)),
        ),
        SizedBox(
          width: 56,
          child: _NumField(
            key: Key('${field.key}_y'),
            value: y,
            isInt: true,
            min: 1,
            onChanged: (v) => onChanged(withAxis(newY: v.toInt())),
          ),
        ),
      ],
    );
  }

  Widget _textField({
    required String initial,
    required TextInputType keyboard,
    required Object? Function(String) parse,
  }) =>
      TextFormField(
        initialValue: initial,
        keyboardType: keyboard,
        style: const TextStyle(color: AraColors.textPrimary, fontSize: 13),
        decoration: const InputDecoration(isDense: true, border: OutlineInputBorder()),
        onChanged: (s) {
          final parsed = parse(s);
          // Ignore an in-progress invalid number (e.g. '' or '-'); commit valid.
          if (parsed != null) onChanged(parsed);
        },
      );

  Widget _label() =>
      Text(field.label, style: const TextStyle(color: AraColors.textSecondary, fontSize: 12));

  Widget _labelled(Widget control) => Column(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [_label(), const SizedBox(height: 4), control],
      );
}

/// `SwitchFilter.Filter` picker. Sources the filter names from the configured
/// filter-wheel slot labels ([filterWheelLabelsProvider]) — the user's filter
/// set from Settings → Filter Wheel — and writes a minimal `FilterInfo`
/// (`{$type, _name, _position}`) on pick. The daemon's `SwitchFilter.MatchFilter`
/// re-resolves the stored filter to the active profile's full `FilterInfo` by
/// name (then position), so only the identifying fields need to be written.
///
/// A stored filter whose name isn't among the configured slots (e.g. a body
/// imported from another rig) is still shown as an extra option so it isn't
/// silently dropped. When no slots are labelled, an instruction to configure
/// them is shown instead of an empty dropdown.
class _FilterEditor extends ConsumerWidget {
  const _FilterEditor({required this.value, required this.onChanged});

  final Object? value;
  final ValueChanged<Object?> onChanged;

  static const String _filterInfoType =
      'OpenAstroAra.Core.Model.Equipment.FilterInfo, OpenAstroAra.Core';

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final labels = ref.watch(filterWheelLabelsProvider);
    // Configured slots → (name, 0-based position) for the dropdown + write-back.
    final slots = <({String name, int position})>[];
    for (var slot = 1; slot <= labels.slotCount; slot++) {
      final name = labels.labelAt(slot);
      if (name.isNotEmpty) slots.add((name: name, position: slot - 1));
    }
    final stored = value is Map ? (value as Map)['_name'] : null;
    final storedName = stored is String && stored.isNotEmpty ? stored : null;

    if (slots.isEmpty && storedName == null) {
      return const Text(
        'No filters configured — set filter names in Settings → Filter Wheel.',
        style: TextStyle(color: AraColors.textDisabled, fontSize: 12),
      );
    }

    final names = slots.map((s) => s.name).toList();
    // Keep an unknown stored filter visible rather than blanking the field.
    final items = [
      if (storedName != null && !names.contains(storedName)) storedName,
      ...names,
    ];

    return DropdownButton<String>(
      value: storedName,
      isExpanded: true,
      hint: const Text('Select a filter',
          style: TextStyle(color: AraColors.textSecondary, fontSize: 13)),
      dropdownColor: AraColors.bgPanel,
      items: [
        for (final name in items)
          DropdownMenuItem(value: name, child: Text(name)),
      ],
      onChanged: (name) {
        // Ignore a pick of the (already-selected) unknown stored filter — there's
        // no slot to resolve a position from, and the existing value stands.
        final slot = slots.where((s) => s.name == name);
        if (slot.isEmpty) return;
        onChanged(<String, dynamic>{
          r'$type': _filterInfoType,
          '_name': slot.first.name,
          '_position': slot.first.position,
        });
      },
    );
  }
}

/// RA (H : M : S) / Dec (± D : M : S) editor over a nested `InputCoordinates`
/// object. Each change rebuilds the whole map (preserving `$type`) and writes it
/// back via [onChanged]. Components are range-clamped (RA h 0–23, d/m 0–59, Dec
/// deg 0–90, seconds capped at 59.999 — the 3-decimal display precision,
/// sub-arcsec); the int/double split matches the C# field types. ±90° forces the
/// Dec minutes/seconds to 0. Field [Key]s are scoped by [fieldKey] so two
/// coordinate editors on one panel (e.g. a slew and an altitude target) can't
/// collide.
class _CoordinatesEditor extends StatelessWidget {
  const _CoordinatesEditor({
    required this.fieldKey,
    required this.value,
    required this.onChanged,
  });

  final String fieldKey;
  final Object? value;
  final ValueChanged<Map<String, dynamic>> onChanged;

  @override
  Widget build(BuildContext context) {
    final c = value is Map ? value as Map : const {};
    final type = c[r'$type'] is String ? c[r'$type'] as String : defaultCoordinates[r'$type'];
    int ri(String k) => c[k] is num ? (c[k] as num).toInt() : 0;
    double rd(String k) => c[k] is num ? (c[k] as num).toDouble() : 0.0;
    final neg = c['NegativeDec'] == true;
    Map<String, dynamic> coord({
      int? raH, int? raM, double? raS, bool? negDec, int? decD, int? decM, double? decS,
    }) {
      var dd = decD ?? ri('DecDegrees');
      var dm = decM ?? ri('DecMinutes');
      var ds = decS ?? rd('DecSeconds');
      // ±90° is the declination pole — minutes/seconds must be 0 there
      // (90:30:00 is invalid). Enforce the cross-field boundary that the
      // per-component clamps can't.
      if (dd >= 90) {
        dd = 90;
        dm = 0;
        ds = 0.0;
      }
      return <String, dynamic>{
        r'$type': type,
        'RAHours': raH ?? ri('RAHours'),
        'RAMinutes': raM ?? ri('RAMinutes'),
        'RASeconds': raS ?? rd('RASeconds'),
        'NegativeDec': negDec ?? neg,
        'DecDegrees': dd,
        'DecMinutes': dm,
        'DecSeconds': ds,
      };
    }
    return Column(
      crossAxisAlignment: CrossAxisAlignment.start,
      children: [
        _axisRow('RA', [
          _NumField(key: Key('${fieldKey}_ra_h'), value: ri('RAHours'), isInt: true, min: 0, max: 23,
              onChanged: (v) => onChanged(coord(raH: v.toInt()))),
          _NumField(key: Key('${fieldKey}_ra_m'), value: ri('RAMinutes'), isInt: true, min: 0, max: 59,
              onChanged: (v) => onChanged(coord(raM: v.toInt()))),
          _NumField(key: Key('${fieldKey}_ra_s'), value: rd('RASeconds'), isInt: false, min: 0, max: 59.999,
              onChanged: (v) => onChanged(coord(raS: v.toDouble()))),
        ]),
        const SizedBox(height: 6),
        _axisRow('Dec', [
          _SignToggle(
            negative: neg,
            onChanged: (n) => onChanged(coord(negDec: n)),
          ),
          _NumField(key: Key('${fieldKey}_dec_d'), value: ri('DecDegrees'), isInt: true, min: 0, max: 90,
              onChanged: (v) => onChanged(coord(decD: v.toInt()))),
          // At the ±90° pole, minutes/seconds must be 0 — disable them so the
          // constraint is visible (rather than silently snapping back on edit).
          _NumField(key: Key('${fieldKey}_dec_m'), value: ri('DecMinutes'), isInt: true, min: 0, max: 59,
              enabled: ri('DecDegrees') < 90,
              onChanged: (v) => onChanged(coord(decM: v.toInt()))),
          _NumField(key: Key('${fieldKey}_dec_s'), value: rd('DecSeconds'), isInt: false, min: 0, max: 59.999,
              enabled: ri('DecDegrees') < 90,
              onChanged: (v) => onChanged(coord(decS: v.toDouble()))),
        ]),
      ],
    );
  }

  static Widget _axisRow(String label, List<Widget> fields) => Row(
        children: [
          SizedBox(
            width: 32,
            child: Text(label, style: const TextStyle(color: AraColors.textSecondary, fontSize: 12)),
          ),
          // Scroll the H/M/S group horizontally so the (wider) Dec row with its
          // sign toggle can't overflow a narrow side pane.
          Expanded(
            child: SingleChildScrollView(
              scrollDirection: Axis.horizontal,
              child: Row(
                children: [
                  for (var i = 0; i < fields.length; i++) ...[
                    if (i > 0) const SizedBox(width: 4),
                    SizedBox(width: 44, child: fields[i]),
                  ],
                ],
              ),
            ),
          ),
        ],
      );
}

/// Composite editor over a nested `WaitLoopData` object (the altitude
/// conditions' `Data`): a `Comparator` dropdown ([altitudeComparators]), a
/// degrees `Offset` (signed — a horizon offset may be negative), and the target
/// `Coordinates` (reusing [_CoordinatesEditor]). Any change rebuilds the whole
/// `Data` map (preserving `$type` and the untouched fields) and writes it back
/// via [onChanged]. Field [Key]s are scoped by [fieldKey].
class _WaitLoopDataEditor extends StatelessWidget {
  const _WaitLoopDataEditor({
    required this.fieldKey,
    required this.value,
    required this.fallbackComparator,
    required this.onChanged,
  });

  final String fieldKey;
  final Object? value;

  /// The Comparator to assume when a stored `Data` has none — this condition's
  /// own catalogued default (AltitudeCondition → LessThan/1, AboveHorizon →
  /// GreaterThan/3), so a body missing the key keeps the right loop direction.
  final int fallbackComparator;
  final ValueChanged<Map<String, dynamic>> onChanged;

  @override
  Widget build(BuildContext context) {
    // Eager copy (not a lazy .cast view) so an off-spec stored value throws at
    // the copy, not at some later element access with an opaque stack trace.
    final data = value is Map
        ? Map<String, dynamic>.from(value as Map)
        : const <String, dynamic>{};
    final type = data[r'$type'] is String ? data[r'$type'] as String : waitLoopDataType;
    // `is num` (not `is int`), matching the Offset/coordinate reads — a JSON
    // serializer may emit the enum as `1.0`, which `is int` would miss. Falls
    // back to this condition's own default comparator (not a fixed GreaterThan).
    final rawComparator = data['Comparator'] is num
        ? (data['Comparator'] as num).toInt()
        : fallbackComparator;
    // For DISPLAY only: coerce an out-of-allow-list comparator (a stale persisted
    // *OrEqual) to this condition's default selectable value so DropdownButton
    // can't assert "no matching item". The RAW value is preserved on write-back
    // below, so touching an unrelated field never silently flips the comparator —
    // only an explicit pick changes it. (fallbackComparator is always 1 or 3.)
    final comparator =
        altitudeComparators.containsKey(rawComparator) ? rawComparator : fallbackComparator;
    final offset = data['Offset'] is num ? (data['Offset'] as num).toDouble() : 0.0;
    final coords = data['Coordinates'];

    Map<String, dynamic> withData({int? newComparator, double? newOffset, Object? newCoords}) =>
        <String, dynamic>{
          r'$type': type,
          'Coordinates': newCoords ?? coords ?? defaultCoordinates,
          'Offset': newOffset ?? offset,
          // Preserve the stored comparator unless the user explicitly picks one.
          'Comparator': newComparator ?? rawComparator,
        };

    return Column(
      crossAxisAlignment: CrossAxisAlignment.start,
      children: [
        const Text('Comparison',
            style: TextStyle(color: AraColors.textSecondary, fontSize: 12)),
        const SizedBox(height: 4),
        DropdownButton<int>(
          key: Key('${fieldKey}_comparator'),
          value: comparator,
          isExpanded: true,
          dropdownColor: AraColors.bgPanel,
          items: [
            for (final e in altitudeComparators.entries)
              DropdownMenuItem(value: e.key, child: Text(e.value)),
          ],
          onChanged: (v) {
            if (v != null) onChanged(withData(newComparator: v));
          },
        ),
        const SizedBox(height: 8),
        const Text('Offset (°)',
            style: TextStyle(color: AraColors.textSecondary, fontSize: 12)),
        const SizedBox(height: 4),
        // Controlled (like the coordinates fields) so the display tracks an
        // external state change; signed because a horizon offset may be negative.
        _NumField(
          key: Key('${fieldKey}_offset'),
          value: offset,
          isInt: false,
          signed: true,
          onChanged: (v) => onChanged(withData(newOffset: v.toDouble())),
        ),
        const SizedBox(height: 8),
        const Text('Coordinates',
            style: TextStyle(color: AraColors.textSecondary, fontSize: 12)),
        const SizedBox(height: 4),
        _CoordinatesEditor(
          fieldKey: '${fieldKey}_coords',
          value: coords,
          onChanged: (c) => onChanged(withData(newCoords: c)),
        ),
      ],
    );
  }
}

class _Placeholder extends StatelessWidget {
  const _Placeholder(this.text);
  final String text;

  @override
  Widget build(BuildContext context) => Padding(
        padding: const EdgeInsets.all(16),
        child: Text(text,
            style: const TextStyle(color: AraColors.textSecondary, fontSize: 13)),
      );
}

/// A controlled non-negative numeric field (int or double) clamped to an
/// optional `[min, max]`. A committed value is always in range: an out-of-range
/// entry snaps to the bound AND the displayed text is corrected, so the field
/// can never diverge from the model. An empty/partial entry is transient
/// (mid-edit) and commits nothing until it parses.
class _NumField extends StatefulWidget {
  const _NumField({
    super.key,
    required this.value,
    required this.onChanged,
    required this.isInt,
    this.min,
    this.max,
    this.enabled = true,
    this.signed = false,
  });
  final num value;
  final ValueChanged<num> onChanged;
  final bool isInt;
  final num? min;
  final num? max;
  final bool enabled;

  /// Allow a leading minus (e.g. an altitude/horizon offset that may be
  /// negative). Off by default — most fields are non-negative.
  final bool signed;

  @override
  State<_NumField> createState() => _NumFieldState();
}

class _NumFieldState extends State<_NumField> {
  late final TextEditingController _controller = TextEditingController(text: _fmt(widget.value));

  String _fmt(num v) {
    // Display the in-range value (same clamp as on commit), so an out-of-range
    // model value — e.g. a server-supplied 59.9995 — shows as 59.999, never a
    // rounded-up 60. The model stays verbatim until the user actually edits.
    final c = _clamp(v);
    if (widget.isInt) return '${c.toInt()}';
    final d = c.toDouble();
    if (d == d.truncateToDouble()) return '${d.toInt()}';
    // TRUNCATE (not round) to ≤3 decimals + strip trailing zeros: a float
    // artifact like 30.300000000000004 shows 30.3, and an in-range 29.9995 shows
    // 29.999 (rounding would flip it to '30' while the model still holds 29.9995).
    // The 1e-9 nudge absorbs IEEE-754 representation noise (30.3 is stored as
    // 30.29999…, so a bare floor would truncate to 30.299); it's far below the
    // 3-decimal resolution, so a genuine 59.9995 still floors to 59.999.
    final t = (d * 1000 + 1e-9).floorToDouble() / 1000;
    if (t == t.truncateToDouble()) return '${t.toInt()}';
    return t
        .toStringAsFixed(3)
        .replaceFirst(RegExp(r'0+$'), '')
        .replaceFirst(RegExp(r'\.$'), '');
  }

  @override
  void didUpdateWidget(_NumField old) {
    super.didUpdateWidget(old);
    final formatted = _fmt(widget.value);
    if (widget.value != old.value && _controller.text != formatted) {
      _controller.text = formatted;
    }
  }

  @override
  void dispose() {
    _controller.dispose();
    super.dispose();
  }

  num _clamp(num v) {
    if (widget.min != null && v < widget.min!) return widget.min!;
    if (widget.max != null && v > widget.max!) return widget.max!;
    return v;
  }

  void _onChanged(String s) {
    final v = widget.isInt ? int.tryParse(s) : double.tryParse(s);
    if (v == null) return; // transient — keep the model, allow retyping
    // A trailing '.' parses (30. → 30.0) but the user is mid-typing the
    // fraction; committing would snap '30.' back to '30' on the next rebuild.
    if (!widget.isInt && s.endsWith('.')) return;
    final clamped = _clamp(v);
    if (clamped != v) {
      final t = _fmt(clamped);
      _controller.value = TextEditingValue(
        text: t,
        selection: TextSelection.collapsed(offset: t.length),
      );
    }
    // Skip a no-op write (e.g. retyping the current value) to avoid a redundant
    // setNodeField + rebuild.
    if (clamped == widget.value) return;
    widget.onChanged(clamped);
  }

  @override
  Widget build(BuildContext context) => TextField(
        controller: _controller,
        enabled: widget.enabled,
        keyboardType:
            TextInputType.numberWithOptions(decimal: !widget.isInt, signed: widget.signed),
        inputFormatters: [
          widget.isInt && !widget.signed
              ? FilteringTextInputFormatter.digitsOnly
              // Digits with at most one decimal point (and an optional leading
              // minus when signed) — rejects the keystroke that would add a
              // second `.`/`-` (keeping the field, not blanking it).
              : _SingleDecimalFormatter(isInt: widget.isInt, signed: widget.signed),
        ],
        style: const TextStyle(color: AraColors.textPrimary, fontSize: 13),
        decoration: const InputDecoration(isDense: true, border: OutlineInputBorder()),
        onChanged: _onChanged,
      );
}

/// Allows a number with at most one `.` (omitted when [isInt]) and an optional
/// leading `-` (only when [signed]). Rejects the edit (keeping the prior text)
/// when the result wouldn't match — so a stray second `.`/`-` is ignored rather
/// than blanking the field, which an anchored `FilteringTextInputFormatter.allow`
/// regex would do.
class _SingleDecimalFormatter extends TextInputFormatter {
  const _SingleDecimalFormatter({this.isInt = false, this.signed = false});
  final bool isInt;
  final bool signed;

  // Compiled once each (not per keystroke); one per (isInt, signed) combination.
  static final RegExp _intUnsigned = RegExp(r'^\d*$');
  static final RegExp _intSigned = RegExp(r'^-?\d*$');
  static final RegExp _decUnsigned = RegExp(r'^\d*\.?\d*$');
  static final RegExp _decSigned = RegExp(r'^-?\d*\.?\d*$');

  RegExp get _pattern => isInt
      ? (signed ? _intSigned : _intUnsigned)
      : (signed ? _decSigned : _decUnsigned);

  @override
  TextEditingValue formatEditUpdate(TextEditingValue oldValue, TextEditingValue newValue) =>
      _pattern.hasMatch(newValue.text) ? newValue : oldValue;
}

/// A compact `+ / −` toggle for the declination sign.
class _SignToggle extends StatelessWidget {
  const _SignToggle({required this.negative, required this.onChanged});
  final bool negative;
  final ValueChanged<bool> onChanged;

  @override
  Widget build(BuildContext context) => OutlinedButton(
        onPressed: () => onChanged(!negative),
        style: OutlinedButton.styleFrom(
          padding: EdgeInsets.zero,
          // shrinkWrap drops the default ~48px of hidden touch padding that would
          // overlap the adjacent field; the explicit 36² minimum keeps it large
          // enough to tap reliably while staying inside its 44px row slot.
          minimumSize: const Size(36, 36),
          tapTargetSize: MaterialTapTargetSize.shrinkWrap,
          foregroundColor: AraColors.textPrimary,
        ),
        child: Text(negative ? '−' : '+', style: const TextStyle(fontSize: 16)),
      );
}
