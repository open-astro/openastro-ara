import 'package:flutter/material.dart';

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
  Widget build(BuildContext context) => Container(
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
  );
}
