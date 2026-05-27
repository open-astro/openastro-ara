import 'package:flutter/material.dart';

import '../../../theme/ara_colors.dart';

/// Placeholder for settings panels whose real form lives in Phase 12h.2.
/// Shows the panel id + a one-paragraph "this lives in 12h.2" note so the
/// tree navigation is testable today.
class GenericPlaceholderPanel extends StatelessWidget {
  final String panelId;
  final String label;
  final String note;

  const GenericPlaceholderPanel({
    super.key,
    required this.panelId,
    required this.label,
    required this.note,
  });

  @override
  Widget build(BuildContext context) {
    return Center(
      child: ConstrainedBox(
        constraints: const BoxConstraints(maxWidth: 480),
        child: Padding(
          padding: const EdgeInsets.all(24),
          child: Column(
            mainAxisSize: MainAxisSize.min,
            children: [
              const Icon(Icons.settings_suggest,
                  size: 64, color: AraColors.textDisabled),
              const SizedBox(height: 12),
              Text(label,
                  style: Theme.of(context).textTheme.headlineSmall),
              const SizedBox(height: 4),
              Text(panelId,
                  style: Theme.of(context).textTheme.labelSmall?.copyWith(
                        color: AraColors.textDisabled,
                      )),
              const SizedBox(height: 16),
              Text(
                note,
                textAlign: TextAlign.center,
                style: Theme.of(context).textTheme.bodyMedium?.copyWith(
                      color: AraColors.textSecondary,
                    ),
              ),
            ],
          ),
        ),
      ),
    );
  }
}
