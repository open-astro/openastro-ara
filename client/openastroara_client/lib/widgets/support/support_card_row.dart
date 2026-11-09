import 'package:flutter/material.dart';

/// A support card's icon and text with its action beside them, or under them
/// in a column too narrow to hold both (Simple Mode's Settings on a phone).
class SupportCardRow extends StatelessWidget {
  const SupportCardRow({
    super.key,
    required this.icon,
    required this.body,
    required this.action,
  });

  static const double _stackBelow = 480;

  final Widget icon;
  final Widget body;
  final Widget action;

  @override
  Widget build(BuildContext context) => LayoutBuilder(
    builder: (context, box) {
      final info = [icon, const SizedBox(width: 12), Expanded(child: body)];
      // Large text needs the same room a narrow column lacks.
      if (box.maxWidth >= MediaQuery.textScalerOf(context).scale(_stackBelow)) {
        return Row(children: [...info, const SizedBox(width: 12), action]);
      }
      return Column(
        crossAxisAlignment: CrossAxisAlignment.stretch,
        children: [
          Row(crossAxisAlignment: CrossAxisAlignment.start, children: info),
          const SizedBox(height: 12),
          action,
        ],
      );
    },
  );
}
