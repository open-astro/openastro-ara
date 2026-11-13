import 'package:flutter/material.dart';

import '../../l10n/l10n.dart';
import '../../util/friendly_error.dart';
import '../../state/settings/loaded_section.dart';

/// ADR 0015 — shown instead of a section's fields while it holds no value from
/// the rig: says so, says why when a load failed, and offers a retry. Never
/// shows defaults.
class SectionNotLoaded extends StatelessWidget {
  const SectionNotLoaded({
    super.key,
    required this.section,
    required this.onRetry,
  });

  final LoadedSection<Object?> section;
  final VoidCallback onRetry;

  @override
  Widget build(BuildContext context) {
    final failure = section.lastFailure;
    return Padding(
      padding: const EdgeInsets.all(24),
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          Text(
            failure == null
                ? 'Loading these settings from your rig…'
                : 'Not loaded — these settings are on your rig and it did not answer. '
                      '${friendlyError(context.l10n, failure, action: context.l10n.actionLoadSettings)}',
          ),
          const SizedBox(height: 12),
          OutlinedButton(
            key: const Key('section-retry'),
            onPressed: onRetry,
            child: const Text('Retry'),
          ),
        ],
      ),
    );
  }
}

/// ADR 0015 — the rig refused a save because another device saved a newer copy.
/// The person's edits stay on screen; Reload replaces them with the rig's copy.
class SectionConflictBanner extends StatelessWidget {
  const SectionConflictBanner({super.key, required this.onReload});

  final VoidCallback onReload;

  @override
  Widget build(BuildContext context) {
    return Padding(
      padding: const EdgeInsets.only(bottom: 12),
      child: Row(
        children: [
          Expanded(
            child: Text(
              'These settings were changed on another device. Nothing was saved. '
              'Reload to see the new values (your edits here will be replaced).',
              style: TextStyle(color: Theme.of(context).colorScheme.error),
            ),
          ),
          TextButton(
            key: const Key('section-reload'),
            onPressed: onReload,
            child: const Text('Reload'),
          ),
        ],
      ),
    );
  }
}
