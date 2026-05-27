import 'package:flutter/material.dart';

import '../../theme/ara_colors.dart';

/// Reusable label/value row for read-only settings panels. Phase 12h.2 uses
/// these across every panel; Phase 12h.2b/c will swap each row for its
/// real editable variant (TextField + StatefulWidget controller, dropdown,
/// slider, etc).
class SettingsRow extends StatelessWidget {
  final String label;
  final String value;
  final String? hint;
  const SettingsRow({
    super.key,
    required this.label,
    required this.value,
    this.hint,
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
                Text(label,
                    style: Theme.of(context).textTheme.bodyMedium?.copyWith(
                          color: AraColors.textSecondary,
                        )),
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
