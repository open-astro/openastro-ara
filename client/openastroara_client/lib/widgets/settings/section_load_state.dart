import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../l10n/l10n.dart';
import '../../util/friendly_error.dart';
import '../../state/profile_management_state.dart';
import '../../state/settings/loaded_section.dart';

/// ADR 0015 — shown instead of a section's fields while it holds no value from
/// the rig: says so, says why when a load failed, and offers a retry. With no
/// rig connected (planning offline) it says only that: there is nothing to
/// load or retry. Never shows defaults.
class SectionNotLoaded extends ConsumerWidget {
  const SectionNotLoaded({
    super.key,
    required this.section,
    required this.onRetry,
    this.padding = const EdgeInsets.all(24),
  });

  final LoadedSection<Object?> section;
  final VoidCallback onRetry;

  /// The default suits a whole panel; a row inside a padded list passes its own.
  final EdgeInsetsGeometry padding;

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    if (ref.watch(profileApiProvider) == null) {
      return Padding(
        padding: padding,
        child: Text(context.l10n.shellRigNotConnected),
      );
    }
    final failure = section.lastFailure;
    return Padding(
      padding: padding,
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          Semantics(
            liveRegion: true,
            child: Text(
              failure == null
                  ? context.l10n.settingsSectionLoading
                  : '${context.l10n.settingsSectionNotLoaded} '
                        '${friendlyError(context.l10n, failure, action: context.l10n.actionLoadSettings)}',
            ),
          ),
          const SizedBox(height: 12),
          OutlinedButton(
            key: const Key('section-retry'),
            onPressed: onRetry,
            child: Text(context.l10n.settingsSectionRetry),
          ),
        ],
      ),
    );
  }
}

/// ADR 0015 — a read-only view of a section another panel edits, while the
/// section holds no copy: [SectionNotLoaded] in place of the rows, with a
/// Retry that loads it again. Never shows defaults.
class SectionNotLoadedRows extends ConsumerWidget {
  const SectionNotLoadedRows(
    this.provider, {
    super.key,
    this.padding = const EdgeInsets.symmetric(vertical: 8),
  });

  final NotifierProvider<SectionNotifier<Object?>, LoadedSection<Object?>>
  provider;
  final EdgeInsetsGeometry padding;

  @override
  Widget build(BuildContext context, WidgetRef ref) => SectionNotLoaded(
    section: ref.watch(provider),
    padding: padding,
    onRetry: () {
      final api = ref.read(profileApiProvider);
      if (api != null) ref.read(provider.notifier).load(api);
    },
  );
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
            child: Semantics(
              liveRegion: true,
              child: Text(
                context.l10n.settingsSectionConflict,
                style: TextStyle(color: Theme.of(context).colorScheme.error),
              ),
            ),
          ),
          TextButton(
            key: const Key('section-reload'),
            onPressed: onReload,
            child: Text(context.l10n.settingsSectionReload),
          ),
        ],
      ),
    );
  }
}
