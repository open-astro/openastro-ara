import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../state/ws/ws_providers.dart';
import '../theme/ara_colors.dart';

/// One line under the equipment bar while the client cannot reach the daemon.
/// The chips grey out at the same moment (their last reading is stale, not
/// wrong), and without a word of explanation that looked like every device
/// dropping at once — three times on 2026-10-16, for minutes each. Hidden
/// (zero height) while the link is up.
class ServerLinkBanner extends ConsumerWidget {
  const ServerLinkBanner({super.key});

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final linkUp = ref.watch(serverLinkUpProvider);
    if (linkUp) return const SizedBox.shrink();
    return Container(
      key: const ValueKey('server-link-banner'),
      width: double.infinity,
      padding: const EdgeInsets.symmetric(horizontal: 12, vertical: 4),
      color: AraColors.accentWarning.withValues(alpha: 0.18),
      child: Row(
        children: [
          const Icon(Icons.link_off, size: 16, color: AraColors.accentWarning),
          const SizedBox(width: 8),
          Expanded(
            child: Text(
              'Server link lost — reconnecting; device status is stale until it is back',
              maxLines: 1,
              overflow: TextOverflow.ellipsis,
              style: Theme.of(context).textTheme.bodySmall,
            ),
          ),
        ],
      ),
    );
  }
}
