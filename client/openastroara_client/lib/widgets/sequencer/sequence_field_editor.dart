/// §38 sequence-editor field panel — edits the scalar fields of the node
/// currently selected in the tree. Each control is driven by the instruction
/// catalog's [InstructionField] schema and writes straight back to the RAW body
/// via [SequenceEditorController.setNodeField]. Complex fields
/// (binning/coordinates/filter) get a placeholder row here; their dedicated
/// editors land in following slices.
library;

import 'package:flutter/material.dart';
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
    final fields =
        def?.fields.where((f) => f.editable).toList() ?? const <InstructionField>[];

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
        else if (fields.isEmpty)
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
      case InstructionFieldType.coordinates:
      case InstructionFieldType.filter:
        return _labelled(
          Text(
            value == null ? '(not set — edited in a later slice)' : '(advanced field)',
            style: const TextStyle(color: AraColors.textDisabled, fontSize: 12),
          ),
        );
    }
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
