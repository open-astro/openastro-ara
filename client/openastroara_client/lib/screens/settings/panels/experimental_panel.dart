import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../../state/simple_mode_state.dart';
import '../../../theme/ara_colors.dart';
import '../../../widgets/settings/settings_row.dart';

/// Settings → System → Experimental: features still being built, off by
/// default and saved on this computer only.
class ExperimentalPanel extends ConsumerWidget {
  const ExperimentalPanel({super.key});

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final theme = Theme.of(context);
    final on = ref.watch(simpleModeFeatureProvider).value ?? false;
    return ListView(
      padding: const EdgeInsets.all(24),
      children: [
        const SettingsSectionHeader('Experimental'),
        Text(
          'Features that are still being built. They may change or go away, '
          'and they only affect this computer.',
          style: theme.textTheme.bodySmall?.copyWith(
            color: AraColors.textSecondary,
          ),
        ),
        const SizedBox(height: 12),
        SwitchListTile(
          contentPadding: EdgeInsets.zero,
          title: const Text('Simple Mode'),
          subtitle: const Text(
            'An image-first screen with only the controls you need tonight. '
            'Adds a Simple Mode button to the top bar, beside Night mode.',
          ),
          value: on,
          onChanged: (v) => ref.read(simpleModeFeatureProvider.notifier).set(v),
        ),
        if (on)
          SwitchListTile(
            key: const Key('experimental-permanent-pier'),
            contentPadding: EdgeInsets.zero,
            title: const Text('Permanent pier'),
            subtitle: const Text(
              'The mount stays polar aligned, so Autopilot skips polar '
              'alignment.',
            ),
            value: ref.watch(simplePermanentPierProvider).value ?? false,
            onChanged: (v) =>
                ref.read(simplePermanentPierProvider.notifier).set(v),
          ),
      ],
    );
  }
}
