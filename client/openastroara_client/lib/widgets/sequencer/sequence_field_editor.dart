/// §38 sequence-editor field panel — edits the scalar fields of the node
/// currently selected in the tree. Each control is driven by the instruction
/// catalog's [InstructionField] schema and writes straight back to the RAW body
/// via [SequenceEditorController.setNodeField]. Binning has an inline `X × Y`
/// editor; the remaining complex fields (coordinates/filter) still get a
/// placeholder row pending their dedicated editors.
library;

import 'package:flutter/material.dart';
import 'package:flutter/services.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../models/sequence/instruction_catalog.dart';
import '../../models/sequence/nina_dom.dart';
import '../../models/sequence/node_display.dart' show nodeLabel;
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
    // Null when the node isn't catalogued; only read in the `def != null` arm.
    final fields = def?.fields.where((f) => f.editable).toList();

    return ListView(
      padding: const EdgeInsets.all(12),
      children: [
        Text(
          nodeLabel(node),
          style: const TextStyle(
              color: AraColors.textPrimary, fontSize: 14, fontWeight: FontWeight.w600),
        ),
        const SizedBox(height: 12),
        if (def == null)
          const _Placeholder('This instruction has no editable fields here.')
        else if (fields!.isEmpty)
          const _Placeholder('No settings — this instruction runs as-is.')
        else
          for (final field in fields)
            Padding(
              padding: const EdgeInsets.only(bottom: 12),
              child: _FieldControl(
                // Fresh control when the selection or field changes.
                key: ValueKey('${selectedPath.join(".")}/${field.key}'),
                field: field,
                value: node[field.key],
                onChanged: (v) => ref
                    .read(sequenceEditorProvider.notifier)
                    .setNodeField(selectedPath, field.key, v),
              ),
            ),
      ],
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
          for (var i = 0; i < fields.length; i++) ...[
            if (i > 0) const SizedBox(width: 4),
            SizedBox(width: 44, child: fields[i]),
          ],
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
    if (widget.isInt) return '${v.toInt()}';
    final d = v.toDouble();
    if (d == d.truncateToDouble()) return '${d.toInt()}';
    // Cap to 3 decimals (sub-arcsec is plenty) and strip trailing zeros, so a
    // float artifact like 30.300000000000004 displays as 30.3.
    return d
        .toStringAsFixed(3)
        .replaceFirst(RegExp(r'0+$'), '')
        .replaceFirst(RegExp(r'\.$'), '');
  }

  @override
  void didUpdateWidget(_NumField old) {
    super.didUpdateWidget(old);
    if (widget.value != old.value && _controller.text != _fmt(widget.value)) {
      _controller.text = _fmt(widget.value);
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
          // Confine the tap target to the box — the default `padded` size adds
          // ~48px of hidden touch area that would overlap the adjacent field.
          minimumSize: Size.zero,
          tapTargetSize: MaterialTapTargetSize.shrinkWrap,
          foregroundColor: AraColors.textPrimary,
        ),
        child: Text(negative ? '−' : '+', style: const TextStyle(fontSize: 16)),
      );
}
