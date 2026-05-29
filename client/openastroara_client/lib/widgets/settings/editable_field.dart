import 'package:flutter/material.dart';
import '../help_icon.dart';
import '../../theme/ara_colors.dart';

/// Shared editable form-row widgets for §25.5.5 settings panels.
///
/// Both `EditableTextRow` and `EditableNumberRow` own their own
/// `TextEditingController` + `FocusNode` per the PR #63 contract (never
/// allocate a controller in `build()`), commit on focus-out + onSubmitted,
/// and **resync from `getCanonical` after commit** so a user-typed value
/// that the setter silently rejects (e.g. negative) snaps back to the
/// kept state — caught on PR #94 round-1 CR.
///
/// `didUpdateWidget` re-syncs when an external state change updates
/// `currentValue` while the user isn't mid-edit.
class EditableTextRow extends StatefulWidget {
  final String label;
  final String currentValue;
  final String Function() getCanonical;
  final void Function(String) parse;
  final int maxLines;
  final String? hint;
  final String? helpKey;

  const EditableTextRow({
    super.key,
    required this.label,
    required this.currentValue,
    required this.getCanonical,
    required this.parse,
    this.maxLines = 1,
    this.hint,
    this.helpKey,
  });

  @override
  State<EditableTextRow> createState() => _EditableTextRowState();
}

class _EditableTextRowState extends State<EditableTextRow> {
  late final TextEditingController _controller;
  late final FocusNode _focusNode;

  @override
  void initState() {
    super.initState();
    _controller = TextEditingController(text: widget.currentValue);
    _focusNode = FocusNode();
    _focusNode.addListener(() {
      if (!_focusNode.hasFocus) _commit();
    });
  }

  void _commit() {
    widget.parse(_controller.text);
    final canonical = widget.getCanonical();
    if (canonical != _controller.text) {
      _controller.text = canonical;
    }
  }

  @override
  void didUpdateWidget(covariant EditableTextRow old) {
    super.didUpdateWidget(old);
    if (!_focusNode.hasFocus && widget.currentValue != _controller.text) {
      _controller.text = widget.currentValue;
    }
  }

  @override
  void dispose() {
    _controller.dispose();
    _focusNode.dispose();
    super.dispose();
  }

  @override
  Widget build(BuildContext context) {
    return Padding(
      padding: const EdgeInsets.symmetric(vertical: 8),
      child: Row(
        crossAxisAlignment: widget.maxLines > 1
            ? CrossAxisAlignment.start
            : CrossAxisAlignment.center,
        children: [
          SizedBox(
            width: 280,
            child: Padding(
              padding: EdgeInsets.only(top: widget.maxLines > 1 ? 12 : 0),
              child: Row(
                children: [
                  Text(
                    widget.label,
                    style: Theme.of(context).textTheme.bodyMedium?.copyWith(
                          color: AraColors.textSecondary,
                        ),
                  ),
                  if (widget.helpKey != null) HelpIcon(helpKey: widget.helpKey!),
                ],
              ),
            ),
          ),
          Expanded(
            child: TextField(
              controller: _controller,
              focusNode: _focusNode,
              maxLines: widget.maxLines,
              decoration: InputDecoration(
                isDense: true,
                hintText: widget.hint,
              ),
              onSubmitted: (_) => _commit(),
            ),
          ),
        ],
      ),
    );
  }
}

/// Single-line numeric editable row. Same lifecycle as EditableTextRow.
class EditableNumberRow extends StatelessWidget {
  final String label;
  final String currentValue;
  final String Function() getCanonical;
  final void Function(String) parse;
  final String? helpKey;
  const EditableNumberRow({
    super.key,
    required this.label,
    required this.currentValue,
    required this.getCanonical,
    required this.parse,
    this.helpKey,
  });

  @override
  Widget build(BuildContext context) {
    return EditableTextRow(
      label: label,
      currentValue: currentValue,
      getCanonical: getCanonical,
      parse: parse,
      helpKey: helpKey,
    );
  }
}

/// Shared enum-dropdown row for settings panels. Lifted from
/// safety_policies_panel's local `_DropdownRow<T>` so panels with enum
/// pickers share the layout.
class SettingsDropdownRow<T> extends StatelessWidget {
  final String label;
  final T value;
  final Map<T, String> items;
  final ValueChanged<T?> onChanged;
  final String? helpKey;
  const SettingsDropdownRow({
    super.key,
    required this.label,
    required this.value,
    required this.items,
    required this.onChanged,
    this.helpKey,
  });

  @override
  Widget build(BuildContext context) {
    return Padding(
      padding: const EdgeInsets.symmetric(vertical: 8),
      child: Row(children: [
        SizedBox(
          width: 280,
          child: Row(
            children: [
              Text(label,
                  style: Theme.of(context).textTheme.bodyMedium?.copyWith(
                        color: AraColors.textSecondary,
                      )),
              if (helpKey != null) HelpIcon(helpKey: helpKey!),
            ],
          ),
        ),
        Expanded(
          child: DropdownButtonFormField<T>(
            initialValue: value,
            isDense: true,
            items: [
              for (final e in items.entries)
                DropdownMenuItem<T>(value: e.key, child: Text(e.value)),
            ],
            onChanged: onChanged,
          ),
        ),
      ]),
    );
  }
}

/// Shared switch row for settings panels. Replaces the per-panel `_SwitchRow`
/// copies that drifted into safety_policies / safety_site / session_filenames
/// / session_notifications / imaging_autofocus / imaging_plate_solve.
///
/// The `hint` slot supports an optional second-line explanation (used by §35
/// safety policies + autofocus to surface playbook-section refs).
class SettingsSwitchRow extends StatelessWidget {
  final String label;
  final bool value;
  final ValueChanged<bool> onChanged;
  final String? hint;
  final String? helpKey;
  const SettingsSwitchRow({
    super.key,
    required this.label,
    required this.value,
    required this.onChanged,
    this.hint,
    this.helpKey,
  });

  @override
  Widget build(BuildContext context) {
    return Padding(
      padding: const EdgeInsets.symmetric(vertical: 4),
      child: Row(children: [
        SizedBox(
          width: 280,
          child: Column(
            crossAxisAlignment: CrossAxisAlignment.start,
            children: [
              Row(
                children: [
                  Text(label,
                      style: Theme.of(context).textTheme.bodyMedium?.copyWith(
                            color: AraColors.textSecondary,
                          )),
                  if (helpKey != null) HelpIcon(helpKey: helpKey!),
                ],
              ),
              if (hint != null)
                Text(hint!,
                    style: Theme.of(context).textTheme.labelSmall?.copyWith(
                          color: AraColors.textDisabled,
                        )),
            ],
          ),
        ),
        Switch(value: value, onChanged: onChanged),
      ]),
    );
  }
}
