import 'dart:async';

import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:url_launcher/url_launcher.dart';

import '../../models/backup_snapshot.dart';
import '../../state/backup/backup_state.dart';
import '../../theme/ara_colors.dart';

/// §43 Backup & Restore — lists the active server's profile-config snapshots
/// (`/api/v1/backup/*`), creates a new backup, downloads a snapshot, and
/// restores selected areas (a confirmed, destructive overwrite of live config).
class BackupRestoreModal extends ConsumerStatefulWidget {
  const BackupRestoreModal({super.key});

  @override
  ConsumerState<BackupRestoreModal> createState() => _BackupRestoreModalState();
}

class _BackupRestoreModalState extends ConsumerState<BackupRestoreModal> {
  bool _creating = false;

  @override
  void initState() {
    super.initState();
    // Re-read on open so a backup created/removed elsewhere shows fresh.
    Future.microtask(() {
      if (mounted) {
        unawaited(ref.read(backupSnapshotsProvider.notifier).refresh());
      }
    });
  }

  Future<void> _createBackup() async {
    setState(() => _creating = true);
    try {
      await ref.read(backupSnapshotsProvider.notifier).createBackup();
      if (mounted) {
        _snack('Backup created.');
      }
    } catch (e) {
      if (mounted) {
        _snack('Backup failed: ${_message(e)}');
      }
    } finally {
      if (mounted) {
        setState(() => _creating = false);
      }
    }
  }

  void _snack(String text) =>
      ScaffoldMessenger.of(context).showSnackBar(SnackBar(content: Text(text)));

  @override
  Widget build(BuildContext context) {
    final async = ref.watch(backupSnapshotsProvider);
    final snapshots = async.asData?.value;

    return Dialog.fullscreen(
      child: Scaffold(
        appBar: AppBar(
          title: const Text('Backup & Restore'),
          leading: IconButton(
            icon: const Icon(Icons.close),
            onPressed: () => Navigator.of(context).pop(),
          ),
          actions: [
            Padding(
              padding: const EdgeInsets.symmetric(horizontal: 8),
              child: TextButton.icon(
                onPressed: (_creating || snapshots == null) ? null : () => unawaited(_createBackup()),
                icon: _creating
                    ? const SizedBox(width: 16, height: 16, child: CircularProgressIndicator(strokeWidth: 2))
                    : const Icon(Icons.add),
                label: const Text('Create backup'),
              ),
            ),
          ],
        ),
        body: _body(context, async, snapshots),
      ),
    );
  }

  Widget _body(BuildContext context, AsyncValue<List<BackupSnapshot>?> async, List<BackupSnapshot>? snapshots) {
    if (async.isLoading && snapshots == null) {
      return const Center(child: CircularProgressIndicator());
    }
    if (async.hasError) {
      return _Centered(
        message: 'Could not load backups.',
        onRetry: () => unawaited(ref.read(backupSnapshotsProvider.notifier).refresh()),
      );
    }
    if (snapshots == null) {
      return const _Centered(message: 'Connect to a server to back up and restore your profile.');
    }
    if (snapshots.isEmpty) {
      return const _Centered(message: 'No backups yet. Use “Create backup” to make one.');
    }
    return ListView(
      padding: const EdgeInsets.all(16),
      children: [for (final s in snapshots) _SnapshotRow(snapshot: s)],
    );
  }
}

class _SnapshotRow extends ConsumerWidget {
  const _SnapshotRow({required this.snapshot});

  final BackupSnapshot snapshot;

  Future<void> _download(BuildContext context, WidgetRef ref) async {
    final api = ref.read(backupApiProvider);
    if (api == null) return;
    final uri = Uri.tryParse(api.absoluteDownloadUrl(snapshot));
    var ok = false;
    if (uri != null) {
      try {
        ok = await launchUrl(uri, mode: LaunchMode.externalApplication);
      } catch (_) {
        // launchUrl can throw (e.g. PlatformException when no handler is registered);
        // since this is fire-and-forget, swallow it and fall through to the snackbar.
        ok = false;
      }
    }
    if (!ok && context.mounted) {
      ScaffoldMessenger.of(context).showSnackBar(const SnackBar(content: Text('Could not open the download.')));
    }
  }

  Future<void> _restore(BuildContext context, WidgetRef ref) async {
    final choice = await showDialog<_RestoreChoice>(
      context: context,
      builder: (_) => _RestoreDialog(snapshot: snapshot),
    );
    if (choice == null || !context.mounted) return;
    try {
      await ref.read(backupSnapshotsProvider.notifier).restore(
            snapshot,
            profiles: choice.profiles,
            sequences: choice.sequences,
          );
      if (context.mounted) {
        ScaffoldMessenger.of(context)
            .showSnackBar(const SnackBar(content: Text('Restore complete. Reconnect equipment if needed.')));
      }
    } catch (e) {
      if (context.mounted) {
        ScaffoldMessenger.of(context).showSnackBar(SnackBar(content: Text('Restore failed: ${_message(e)}')));
      }
    }
  }

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final created = snapshot.createdUtc;
    return Card(
      child: ListTile(
        title: Text(created != null ? _formatDate(created) : snapshot.backupId),
        subtitle: Column(
          crossAxisAlignment: CrossAxisAlignment.start,
          children: [
            const SizedBox(height: 4),
            Text('${_formatBytes(snapshot.sizeBytes)} · ${snapshot.includedAreas.join(', ')}',
                style: Theme.of(context).textTheme.bodySmall?.copyWith(color: AraColors.textSecondary)),
          ],
        ),
        trailing: Row(
          mainAxisSize: MainAxisSize.min,
          children: [
            IconButton(
              tooltip: 'Download',
              icon: const Icon(Icons.download),
              onPressed: () => unawaited(_download(context, ref)),
            ),
            TextButton(
              onPressed: () => unawaited(_restore(context, ref)),
              child: const Text('Restore'),
            ),
          ],
        ),
      ),
    );
  }
}

/// The areas the user chose to restore (returned from [_RestoreDialog]).
class _RestoreChoice {
  const _RestoreChoice({required this.profiles, required this.sequences});
  final bool profiles;
  final bool sequences;
}

/// Destructive-restore confirmation: pick which captured areas to overwrite.
class _RestoreDialog extends StatefulWidget {
  const _RestoreDialog({required this.snapshot});
  final BackupSnapshot snapshot;

  @override
  State<_RestoreDialog> createState() => _RestoreDialogState();
}

class _RestoreDialogState extends State<_RestoreDialog> {
  late bool _profiles = _hasProfiles;
  late bool _sequences = _hasSequences;

  bool get _hasProfiles => widget.snapshot.includedAreas.contains(BackupAreas.profiles);
  bool get _hasSequences => widget.snapshot.includedAreas.contains(BackupAreas.sequences);

  @override
  Widget build(BuildContext context) {
    return AlertDialog(
      title: const Text('Restore backup'),
      content: Column(
        mainAxisSize: MainAxisSize.min,
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          const Text('This overwrites the selected areas of your live profile. This cannot be undone.'),
          const SizedBox(height: 8),
          if (_hasProfiles)
            CheckboxListTile(
              contentPadding: EdgeInsets.zero,
              title: const Text('Profile settings'),
              value: _profiles,
              onChanged: (v) => setState(() => _profiles = v ?? false),
            ),
          if (_hasSequences)
            CheckboxListTile(
              contentPadding: EdgeInsets.zero,
              title: const Text('Sequences'),
              value: _sequences,
              onChanged: (v) => setState(() => _sequences = v ?? false),
            ),
        ],
      ),
      actions: [
        TextButton(onPressed: () => Navigator.of(context).pop(), child: const Text('Cancel')),
        FilledButton(
          // Disabled until at least one area is selected — an empty restore is a 422.
          onPressed: (_profiles || _sequences)
              ? () => Navigator.of(context).pop(_RestoreChoice(profiles: _profiles, sequences: _sequences))
              : null,
          child: const Text('Restore'),
        ),
      ],
    );
  }
}

class _Centered extends StatelessWidget {
  const _Centered({required this.message, this.onRetry});
  final String message;
  final VoidCallback? onRetry;

  @override
  Widget build(BuildContext context) {
    return Center(
      child: Column(
        mainAxisSize: MainAxisSize.min,
        children: [
          Text(message, textAlign: TextAlign.center, style: Theme.of(context).textTheme.bodyMedium),
          if (onRetry != null) ...[
            const SizedBox(height: 12),
            FilledButton(onPressed: onRetry, child: const Text('Retry')),
          ],
        ],
      ),
    );
  }
}

String _message(Object e) => e.toString().replaceFirst('Exception: ', '');

String _formatDate(DateTime utc) {
  final l = utc.toLocal();
  String two(int n) => n.toString().padLeft(2, '0');
  return '${l.year}-${two(l.month)}-${two(l.day)} ${two(l.hour)}:${two(l.minute)}';
}

String _formatBytes(int bytes) {
  if (bytes < 1024) return '$bytes B';
  const units = ['KB', 'MB', 'GB', 'TB'];
  var size = bytes / 1024;
  var i = 0;
  while (size >= 1024 && i < units.length - 1) {
    size /= 1024;
    i++;
  }
  return '${size.toStringAsFixed(size >= 10 ? 0 : 1)} ${units[i]}';
}
