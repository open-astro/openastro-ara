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
/// Dismiss is session-local and keyed by the bridge version (per the §30.7 silent-
/// banner pattern): dismissing hides this version, but a later warn carrying a
/// *different* version re-shows so the user isn't kept in the dark about a changed
/// hub. Returns an empty box when there's no warning or it's been dismissed.
class AlpacaBridgeWarningBanner extends ConsumerStatefulWidget {
  const AlpacaBridgeWarningBanner({super.key});

  @override
  ConsumerState<AlpacaBridgeWarningBanner> createState() =>
      _AlpacaBridgeWarningBannerState();
}

class _AlpacaBridgeWarningBannerState
    extends ConsumerState<AlpacaBridgeWarningBanner> {
  String? _dismissedVersion;

  @override
  Widget build(BuildContext context) {
    final warning = ref.watch(alpacaBridgeWarningProvider);
    if (warning == null || warning.version == _dismissedVersion) {
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
            onPressed: () =>
                setState(() => _dismissedVersion = warning.version),
            icon: const Icon(Icons.close, size: 18),
            tooltip: 'Dismiss',
          ),
        ],
      ),
    );
  }
}
