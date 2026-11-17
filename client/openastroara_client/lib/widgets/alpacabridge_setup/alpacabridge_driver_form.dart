import 'package:flutter/material.dart';

import '../../l10n/l10n.dart';
import '../../models/alpacabridge_form.dart';
import '../../models/alpacabridge_setup.dart';
import '../../screens/wizard/wizard_form_kit.dart';

/// #1580 — the form AlpacaBridge's catalog describes for one [driver] (rules in [BridgeForm]).
/// [onSubmit] gets the visible fields' values and, apart, the secrets: a secret never rides in the values.
class AlpacaBridgeDriverForm extends StatefulWidget {
  const AlpacaBridgeDriverForm({
    super.key,
    required this.driver,
    required this.onSubmit,
    this.hide = const {},
    this.initialValues = const {},
    this.submitLabel,
  });

  final BridgeDriver driver;
  final void Function(Map<String, Object?> values, Map<String, String> secrets)
  onSubmit;

  /// Fields the caller already knows (a detected serial): not shown.
  final Set<String> hide;
  final Map<String, Object?> initialValues;
  final String? submitLabel;

  @override
  State<AlpacaBridgeDriverForm> createState() => _AlpacaBridgeDriverFormState();
}

class _AlpacaBridgeDriverFormState extends State<AlpacaBridgeDriverForm> {
  late final BridgeForm _form = BridgeForm(widget.driver);
  late final Map<String, Object?> _values = {
    ..._form.initialValues(),
    ...widget.initialValues,
  };
  final _secrets = <String, String>{};
  Map<String, String> _errors = const {};

  void _submit() {
    final all = {..._values, ..._secrets};
    final errors = _form.errors(all);
    if (errors.isNotEmpty) {
      setState(() => _errors = errors);
      return;
    }
    final visible = _form.visibleFields(_values).map((f) => f.key).toSet();
    widget.onSubmit(
      {
        for (final e in _values.entries)
          if (visible.contains(e.key) && !_secretKeys.contains(e.key))
            e.key: e.value,
      },
      {
        for (final e in _secrets.entries)
          if (e.value.isNotEmpty && visible.contains(e.key)) e.key: e.value,
      },
    );
  }

  Set<String> get _secretKeys => {
    for (final f in widget.driver.fields)
      if (f.secret) f.key,
  };

  String? _error(String key) {
    final l = context.l10n;
    return switch (_errors[key]) {
      BridgeForm.required => l.bridgeSetupRequired,
      BridgeForm.outOfRange => l.bridgeSetupOutOfRange,
      BridgeForm.notAllowed => l.bridgeSetupNotAllowed,
      _ => null,
    };
  }

  @override
  Widget build(BuildContext context) {
    final l = context.l10n;
    return Column(
      crossAxisAlignment: CrossAxisAlignment.stretch,
      children: [
        for (final f in _form.visibleFields(_values))
          if (!widget.hide.contains(f.key))
            _field(f, _values, '', (k, v) => setState(() => _values[k] = v)),
        const SizedBox(height: 8),
        Align(
          alignment: Alignment.centerLeft,
          child: FilledButton(
            key: const Key('form-submit'),
            style: FilledButton.styleFrom(minimumSize: const Size(0, 48)),
            onPressed: _submit,
            child: Text(widget.submitLabel ?? l.bridgeSetupContinue),
          ),
        ),
      ],
    );
  }

  /// One field. [values] is the map it lives in (the form's, or one row of a record list).
  Widget _field(
    BridgeField f,
    Map<String, Object?> values,
    String prefix,
    void Function(String key, Object? value) set,
  ) {
    final key = '$prefix${f.key}';
    final error = _error(key);
    final Widget input;
    if (f.type == BridgeFieldType.recordList) {
      input = _records(f, values, key);
    } else if (f.enumValues != null) {
      input = WizardDropdown<String>(
        label: f.key,
        value: '${values[f.key] ?? f.enumValues!.first}',
        entries: [
          for (final v in f.enumValues!) DropdownMenuEntry(value: v, label: v),
        ],
        helperText: error,
        onChanged: (v) => set(f.key, v),
      );
    } else if (f.type == BridgeFieldType.boolean) {
      input = SwitchListTile(
        title: Text(f.key),
        value: values[f.key] == true,
        onChanged: (v) => set(f.key, v),
      );
    } else {
      final numeric =
          f.type == BridgeFieldType.integer || f.type == BridgeFieldType.number;
      final initial = values[f.key];
      input = WizardTextField(
        label: f.key,
        required: f.required,
        obscureText: f.secret,
        errorText: error,
        initialValue: initial is List
            ? initial.join(', ')
            : initial == null
            ? null
            : '$initial',
        keyboardType: numeric
            ? const TextInputType.numberWithOptions(signed: true, decimal: true)
            : null,
        onChanged: (t) {
          if (f.secret) {
            _secrets[f.key] = t;
          } else if (numeric) {
            set(
              f.key,
              f.type == BridgeFieldType.integer
                  ? int.tryParse(t)
                  : double.tryParse(t),
            );
          } else if (f.type == BridgeFieldType.stringList) {
            set(f.key, [
              for (final s in t.split(','))
                if (s.trim().isNotEmpty) s.trim(),
            ]);
          } else {
            set(f.key, t);
          }
        },
      );
    }
    return KeyedSubtree(key: Key('form-field-$key'), child: input);
  }

  Widget _records(BridgeField f, Map<String, Object?> values, String key) {
    final l = context.l10n;
    final rows = (values[f.key] as List? ?? const [])
        .cast<Map<String, Object?>>();
    return Column(
      crossAxisAlignment: CrossAxisAlignment.start,
      children: [
        Text(f.key),
        for (var i = 0; i < rows.length; i++)
          Row(
            crossAxisAlignment: CrossAxisAlignment.start,
            children: [
              Expanded(
                child: Column(
                  children: [
                    for (final sub in BridgeForm.visibleOf(f.fields, rows[i]))
                      _field(
                        sub,
                        rows[i],
                        '$key[$i].',
                        (k, v) => setState(() => rows[i][k] = v),
                      ),
                  ],
                ),
              ),
              IconButton(
                tooltip: l.bridgeSetupRemoveRow,
                icon: const Icon(Icons.remove_circle_outline),
                onPressed: () => setState(() => rows.removeAt(i)),
              ),
            ],
          ),
        TextButton.icon(
          style: TextButton.styleFrom(minimumSize: const Size(0, 48)),
          onPressed: () => setState(() {
            values[f.key] = [
              ...rows,
              <String, Object?>{
                for (final sub in f.fields)
                  if (sub.defaultValue != null) sub.key: sub.defaultValue,
              },
            ];
          }),
          icon: const Icon(Icons.add),
          label: Text(l.bridgeSetupAddRow),
        ),
      ],
    );
  }
}
