/// §38 sequence-editor field panel — edits the scalar fields of the node
/// currently selected in the tree. Each control is driven by the instruction
/// catalog's [InstructionField] schema and writes straight back to the RAW body
/// via [SequenceEditorController.setNodeField]. Binning has an inline `X × Y`
/// editor; the remaining complex fields (coordinates/filter) still get a
/// placeholder row pending their dedicated editors.
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
        return _labelled(_textField(
          initial: value == null ? '' : '$value',
          keyboard: const TextInputType.numberWithOptions(decimal: true, signed: true),
          parse: (s) => double.tryParse(s),
        ));
      case InstructionFieldType.integer:
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
        return _labelled(_coordinatesEditor());
      case InstructionFieldType.filter:
        return _labelled(
          Text(
            value == null ? '(not set — edited in a later slice)' : '(advanced field)',
            style: const TextStyle(color: AraColors.textDisabled, fontSize: 12),
          ),
        );
    }
  }

  /// RA (H : M : S) / Dec (± D : M : S) editor over the nested `InputCoordinates`
  /// object. Each change rebuilds the whole map (preserving `$type`) and writes
  /// it back via [onChanged]. Components are range-clamped (RA h 0–23, d/m 0–59,
  /// Dec deg 0–90, seconds capped at 59.999 — the 3-decimal display precision,
  /// sub-arcsec); the int/double split matches the C# field types. ±90° forces
  /// the Dec minutes/seconds to 0.
  Widget _coordinatesEditor() {
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
          _NumField(key: Key('${field.key}_ra_h'), value: ri('RAHours'), isInt: true, min: 0, max: 23,
              onChanged: (v) => onChanged(coord(raH: v.toInt()))),
          _NumField(key: Key('${field.key}_ra_m'), value: ri('RAMinutes'), isInt: true, min: 0, max: 59,
              onChanged: (v) => onChanged(coord(raM: v.toInt()))),
          _NumField(key: Key('${field.key}_ra_s'), value: rd('RASeconds'), isInt: false, min: 0, max: 59.999,
              onChanged: (v) => onChanged(coord(raS: v.toDouble()))),
        ]),
        const SizedBox(height: 6),
        _axisRow('Dec', [
          _SignToggle(
            negative: neg,
            onChanged: (n) => onChanged(coord(negDec: n)),
          ),
          _NumField(key: Key('${field.key}_dec_d'), value: ri('DecDegrees'), isInt: true, min: 0, max: 90,
              onChanged: (v) => onChanged(coord(decD: v.toInt()))),
          // At the ±90° pole, minutes/seconds must be 0 — disable them so the
          // constraint is visible (rather than silently snapping back on edit).
          _NumField(key: Key('${field.key}_dec_m'), value: ri('DecMinutes'), isInt: true, min: 0, max: 59,
              enabled: ri('DecDegrees') < 90,
              onChanged: (v) => onChanged(coord(decM: v.toInt()))),
          _NumField(key: Key('${field.key}_dec_s'), value: rd('DecSeconds'), isInt: false, min: 0, max: 59.999,
              enabled: ri('DecDegrees') < 90,
              onChanged: (v) => onChanged(coord(decS: v.toDouble()))),
        ]),
      ],
    );
  }

  Widget _axisRow(String label, List<Widget> fields) => Row(
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
  });
  final num value;
  final ValueChanged<num> onChanged;
  final bool isInt;
  final num? min;
  final num? max;
  final bool enabled;

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
        keyboardType: TextInputType.numberWithOptions(decimal: !widget.isInt),
        inputFormatters: [
          widget.isInt
              ? FilteringTextInputFormatter.digitsOnly
              // Digits with at most one decimal point — rejects the keystroke
              // that would add a second `.` (keeping the field, not blanking it).
              : const _SingleDecimalFormatter(),
        ],
        style: const TextStyle(color: AraColors.textPrimary, fontSize: 13),
        decoration: const InputDecoration(isDense: true, border: OutlineInputBorder()),
        onChanged: _onChanged,
      );
}

/// Allows a non-negative decimal with at most one `.`. Rejects the edit
/// (keeping the prior text) when the result wouldn't parse — so a stray second
/// `.` is ignored rather than blanking the field, which an anchored
/// `FilteringTextInputFormatter.allow` regex would do.
class _SingleDecimalFormatter extends TextInputFormatter {
  const _SingleDecimalFormatter();
  static final RegExp _ok = RegExp(r'^\d*\.?\d*$');

  @override
  TextEditingValue formatEditUpdate(TextEditingValue oldValue, TextEditingValue newValue) =>
      _ok.hasMatch(newValue.text) ? newValue : oldValue;
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
