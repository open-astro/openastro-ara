import 'package:flutter/material.dart';

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

  const EditableTextRow({
    super.key,
    required this.label,
    required this.currentValue,
    required this.getCanonical,
    required this.parse,
    this.maxLines = 1,
    this.hint,
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
              child: Text(
                widget.label,
                style: Theme.of(context).textTheme.bodyMedium?.copyWith(
                      color: AraColors.textSecondary,
                    ),
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
  const EditableNumberRow({
    super.key,
    required this.label,
    required this.currentValue,
    required this.getCanonical,
    required this.parse,
  });

  @override
  Widget build(BuildContext context) {
    return EditableTextRow(
      label: label,
      currentValue: currentValue,
      getCanonical: getCanonical,
      parse: parse,
    );
  }
}
