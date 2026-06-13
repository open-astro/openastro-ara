import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../state/equipment/alpaca_bridge_warning_state.dart';
import '../../theme/ara_colors.dart';

/// §68.2 — dismissible advisory banner shown when a device connected through an
/// AlpacaBridge in the 1.2–1.5 band (supported, but a newer hub is recommended).
/// Driven by the `equipment.alpaca_bridge_outdated_warn` WS event via
/// [alpacaBridgeWarningProvider]. Equipment still connects — this is purely
/// advisory.
///
/// Dismiss is keyed by bridge version and held in
/// [alpacaBridgeWarningDismissedVersionProvider] (not widget state) so it survives
/// the banner being torn down + remounted as the user navigates between settings
/// panels. A later warn carrying a *different* version re-shows. Returns an empty
/// box when there's no warning or it's been dismissed.
class AlpacaBridgeWarningBanner extends ConsumerWidget {
  const AlpacaBridgeWarningBanner({super.key});

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final warning = ref.watch(alpacaBridgeWarningProvider);
    final dismissedVersion = ref.watch(alpacaBridgeWarningDismissedVersionProvider);
    if (warning == null || warning.version == dismissedVersion) {
      return const SizedBox.shrink();
    }

    return Container(
      decoration: BoxDecoration(
        color: AraColors.accentBusy.withValues(alpha: 0.12),
        border: const Border(bottom: BorderSide(color: AraColors.border)),
      ),
      padding: const EdgeInsets.symmetric(horizontal: 16, vertical: 10),
      child: Row(
        children: [
          const Icon(Icons.info_outline, size: 18, color: AraColors.accentBusy),
          const SizedBox(width: 12),
          Expanded(
            child: Text(
              'AlpacaBridge ${warning.version} detected — version '
              '${warning.recommended}+ recommended. Equipment still connects, '
              'but updating AlpacaBridge is advised.',
            ),
          ),
          IconButton(
            onPressed: () => ref
                .read(alpacaBridgeWarningDismissedVersionProvider.notifier)
                .dismiss(warning.version),
            icon: const Icon(Icons.close, size: 18),
            tooltip: 'Dismiss',
          ),
        ],
      ),
    );
  }
}
