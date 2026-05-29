import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../help_icon.dart';
import '../../state/settings/settings_nav.dart';
import '../../theme/ara_colors.dart';

/// Reusable label/value row for read-only settings panels.
class SettingsRow extends StatelessWidget {
  final String label;
  final String value;
  final String? hint;
  final String? helpKey;
  const SettingsRow({
    super.key,
    required this.label,
    required this.value,
    this.hint,
    this.helpKey,
  });

  @override
  Widget build(BuildContext context) {
    return Padding(
      padding: const EdgeInsets.symmetric(vertical: 8),
      child: Row(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
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
                  Padding(
                    padding: const EdgeInsets.only(top: 2),
                    child: Text(
                      hint!,
                      style: Theme.of(context).textTheme.labelSmall?.copyWith(
                            color: AraColors.textDisabled,
                          ),
                    ),
                  ),
              ],
            ),
          ),
          Expanded(
            child: Text(value, style: Theme.of(context).textTheme.bodyMedium),
          ),
        ],
      ),
    );
  }
}

/// Section divider with title, for grouping rows inside a panel.
class SettingsSectionHeader extends StatelessWidget {
  final String title;
  const SettingsSectionHeader(this.title, {super.key});

  @override
  Widget build(BuildContext context) {
    return Padding(
      padding: const EdgeInsets.fromLTRB(0, 16, 0, 8),
      child: Text(
        title,
        style: Theme.of(context).textTheme.titleSmall?.copyWith(
              color: AraColors.textPrimary,
              letterSpacing: 0.4,
            ),
      ),
    );
  }
}


/// Wrapper that adds a highlight border when its [settingId] is targetted by
/// the search registry. Per §61.2, highlight is a yellow border that fades.
class SearchHighlight extends ConsumerWidget {
  final String? settingId;
  final Widget child;

  const SearchHighlight({
    super.key,
    required this.settingId,
    required this.child,
  });

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    if (settingId == null) return child;
    final highlightedId = ref.watch(highlightedSettingProvider);
    final isHighlighted = highlightedId == settingId;

    return AnimatedContainer(
      duration: const Duration(milliseconds: 300),
      decoration: BoxDecoration(
        border: Border.all(
          color: isHighlighted
              ? Colors.yellow.withValues(alpha: 0.8)
              : Colors.transparent,
          width: 2,
        ),
        borderRadius: BorderRadius.circular(4),
      ),
      padding: const EdgeInsets.symmetric(horizontal: 4),
      child: child,
    );
  }
}
