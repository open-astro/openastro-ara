import 'package:flutter/material.dart';
import 'package:flutter/semantics.dart';

import '../theme/ara_colors.dart';
import '../theme/ara_metrics.dart';

/// A choice made where it is asked (Simple Mode has no popups): one radio row
/// per option, each at least 48 px tall.
class InlineChoices<T> extends StatelessWidget {
  const InlineChoices({
    super.key,
    required this.id,
    required this.value,
    required this.options,
    required this.onChanged,
    this.detail,
    this.style,
  });

  /// Keys the rows: `<id>-0`, `<id>-1`...
  final String id;
  final T value;
  final List<(T, String)> options;

  /// Null greys the rows out.
  final ValueChanged<T>? onChanged;

  /// A line under an option's label.
  final String? Function(T)? detail;
  final TextStyle? style;

  @override
  Widget build(BuildContext context) => Material(
    type: MaterialType.transparency,
    child: RadioGroup<T>(
      groupValue: value,
      onChanged: (v) {
        if (v != null && v != value) onChanged?.call(v as T);
      },
      child: Column(
        mainAxisSize: MainAxisSize.min,
        crossAxisAlignment: CrossAxisAlignment.stretch,
        children: [
          for (final (i, o) in options.indexed)
            RadioListTile<T>(
              key: Key('$id-$i'),
              value: o.$1,
              enabled: onChanged != null,
              dense: true,
              contentPadding: EdgeInsets.zero,
              title: Text(o.$2, style: style),
              subtitle: switch (detail?.call(o.$1)) {
                final d? => Text(d, style: AraText.caption),
                null => null,
              },
            ),
        ],
      ),
    ),
  );
}

/// What a dialog gives for free, for a block that opens in place: it scrolls
/// into view, takes the focus (Tab lands on its first control) and says
/// [title] to screen readers. Closing hands the focus back.
class InlineAttention extends StatefulWidget {
  const InlineAttention({super.key, required this.title, required this.child});

  final String title;
  final Widget child;

  @override
  State<InlineAttention> createState() => _InlineAttentionState();
}

class _InlineAttentionState extends State<InlineAttention> {
  final _focus = FocusNode(debugLabel: 'Inline block', skipTraversal: true);
  FocusNode? _opener;

  @override
  void initState() {
    super.initState();
    WidgetsBinding.instance.addPostFrameCallback((_) {
      if (!mounted) return;
      // Its end, then its start: one taller than the view keeps its title.
      for (final policy in const [
        ScrollPositionAlignmentPolicy.keepVisibleAtEnd,
        ScrollPositionAlignmentPolicy.keepVisibleAtStart,
      ]) {
        Scrollable.ensureVisible(context, alignmentPolicy: policy);
      }
      _opener = FocusManager.instance.primaryFocus;
      // The opener holds the focus, so autofocus wouldn't move it.
      _focus.requestFocus();
      SemanticsService.sendAnnouncement(
        View.of(context),
        widget.title,
        Directionality.of(context),
      );
    });
  }

  @override
  void deactivate() {
    if (_focus.hasFocus && _opener?.context != null) _opener!.requestFocus();
    super.deactivate();
  }

  @override
  void dispose() {
    _focus.dispose();
    super.dispose();
  }

  @override
  Widget build(BuildContext context) =>
      Focus(focusNode: _focus, child: widget.child);
}

/// A question answered where it was asked: its words, then 44 px Cancel and
/// Confirm. Keys `<id>-confirm` and `<id>-cancel`.
class InlineConfirm extends StatelessWidget {
  const InlineConfirm({
    super.key,
    required this.id,
    required this.title,
    required this.body,
    required this.confirmLabel,
    required this.cancelLabel,
    required this.onConfirm,
    required this.onCancel,
    this.style,
  });

  final String id;
  final String title;
  final Widget body;
  final String confirmLabel;
  final String cancelLabel;
  final VoidCallback onConfirm;
  final VoidCallback onCancel;
  final TextStyle? style;

  @override
  Widget build(BuildContext context) => InlineAttention(
    title: title,
    child: Container(
      padding: const EdgeInsets.all(AraSpace.s12),
      decoration: BoxDecoration(
        color: AraColors.tileFill,
        borderRadius: BorderRadius.circular(10),
      ),
      child: Column(
        mainAxisSize: MainAxisSize.min,
        crossAxisAlignment: CrossAxisAlignment.stretch,
        spacing: AraSpace.s8,
        children: [
          Text(
            title,
            style: (style ?? const TextStyle()).copyWith(
              fontWeight: FontWeight.w600,
            ),
          ),
          DefaultTextStyle.merge(style: style, child: body),
          Row(
            mainAxisAlignment: MainAxisAlignment.end,
            spacing: AraSpace.s8,
            children: [
              TextButton(
                key: Key('$id-cancel'),
                style: TextButton.styleFrom(minimumSize: const Size(0, 44)),
                onPressed: onCancel,
                child: Text(cancelLabel),
              ),
              FilledButton(
                key: Key('$id-confirm'),
                style: FilledButton.styleFrom(minimumSize: const Size(0, 44)),
                onPressed: onConfirm,
                child: Text(confirmLabel),
              ),
            ],
          ),
        ],
      ),
    ),
  );
}
