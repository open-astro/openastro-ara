import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../state/launch_gate_state.dart';
import '../state/profile_cache_state.dart';

/// §2 "Plan offline" entry — self-gating: enabled only when at least one
/// profile is cached on this device (maintainer call: offline planning
/// without a profile just ranks and drafts against meaningless defaults).
/// Disabled it stays visible with the why in a tooltip, so the feature is
/// discoverable rather than mysteriously absent on a fresh install.
class PlanOfflineButton extends ConsumerWidget {
  const PlanOfflineButton({super.key, this.label = 'Plan offline'});

  final String label;

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final hasProfiles = ref.watch(cachedProfilesProvider).maybeWhen(
          data: (cached) => cached.profiles.isNotEmpty,
          orElse: () => false,
        );
    final button = TextButton.icon(
      onPressed: hasProfiles
          ? () => ref.read(offlineModeProvider.notifier).enter()
          : null,
      icon: const Icon(Icons.cloud_off_outlined, size: 18),
      label: Text(label),
    );
    if (hasProfiles) return button;
    return Tooltip(
      message: 'Offline planning needs a profile — connect to your server '
          'once and set one up first.',
      child: button,
    );
  }
}
