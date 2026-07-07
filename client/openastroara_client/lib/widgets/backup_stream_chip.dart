import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../state/backup/backup_stream_state.dart';
import '../theme/ara_colors.dart';

/// §44 footer chip — the backup stream's live pulse on the always-visible
/// status bar. Hidden entirely while the stream is disabled (a rig that
/// never enabled it shouldn't carry a dead indicator); otherwise shows the
/// mirror's health at a glance: syncing progress, all-clear, or the problem
/// in error color.
class BackupStreamChip extends ConsumerWidget {
  const BackupStreamChip({super.key});

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final stream = ref.watch(backupStreamProvider);
    if (!stream.enabled && stream.problem == null) {
      return const SizedBox.shrink();
    }

    final IconData icon;
    final Color color;
    final String text;
    if (stream.problem != null) {
      icon = Icons.cloud_off;
      color = AraColors.accentError;
      text = 'Backup: attention';
    } else if (!stream.active) {
      icon = Icons.cloud_queue;
      color = AraColors.textDisabled;
      text = 'Backup: connecting…';
    } else if (stream.pendingCount > 0) {
      icon = Icons.cloud_upload;
      color = AraColors.accentInfo;
      text = 'Backup: ${stream.pendingCount} pending';
    } else {
      icon = Icons.cloud_done;
      color = AraColors.accentConnected;
      text = 'Backup: up to date';
    }

    return Tooltip(
      message: stream.problem ??
          '${stream.syncedThisSession} frame(s) mirrored this session'
              '${stream.pendingCount > 0 ? ', ${stream.pendingCount} pending' : ''}.',
      child: Padding(
        padding: const EdgeInsets.symmetric(horizontal: 8),
        child: Row(
          mainAxisSize: MainAxisSize.min,
          children: [
            Icon(icon, size: 14, color: color),
            const SizedBox(width: 4),
            Text(
              text,
              style: Theme.of(context)
                  .textTheme
                  .bodySmall
                  ?.copyWith(color: color),
            ),
          ],
        ),
      ),
    );
  }
}
