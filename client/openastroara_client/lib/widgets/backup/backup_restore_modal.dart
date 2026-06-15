import 'dart:async';

import 'package:dio/dio.dart';
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
  const BackupRestoreModal({
    super.key,
    this.restorePollInterval = const Duration(milliseconds: 500),
    this.restorePollTimeout = const Duration(minutes: 5),
  });

  /// §43-2b restore-progress poll cadence + deadline. Overridable so tests don't
  /// wall-clock wait (production uses the defaults).
  final Duration restorePollInterval;
  final Duration restorePollTimeout;

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
    final asyncValue = ref.watch(backupSnapshotsProvider);
    final snapshots = asyncValue.asData?.value;

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
        body: _body(context, asyncValue, snapshots),
      ),
    );
  }

  Widget _body(BuildContext context, AsyncValue<List<BackupSnapshot>?> asyncValue, List<BackupSnapshot>? snapshots) {
    if (asyncValue.isLoading && snapshots == null) {
      return const Center(child: CircularProgressIndicator());
    }
    if (asyncValue.hasError) {
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
      // Keyed by id: _SnapshotRow is stateful (_restoring/_downloading), so without a key
      // its State would re-associate with the wrong snapshot when the list shifts (a new
      // backup prepends after createBackup).
      children: [
        for (final s in snapshots)
          _SnapshotRow(
            key: ValueKey(s.backupId),
            snapshot: s,
            restorePollInterval: widget.restorePollInterval,
            restorePollTimeout: widget.restorePollTimeout,
          ),
      ],
    );
  }
}

class _SnapshotRow extends ConsumerStatefulWidget {
  const _SnapshotRow({
    super.key,
    required this.snapshot,
    required this.restorePollInterval,
    required this.restorePollTimeout,
  });

  final BackupSnapshot snapshot;
  final Duration restorePollInterval;
  final Duration restorePollTimeout;

  @override
  ConsumerState<_SnapshotRow> createState() => _SnapshotRowState();
}

class _SnapshotRowState extends ConsumerState<_SnapshotRow> {
  // Guards a second Restore tap while one is running. Restore is a destructive
  // overwrite of live config; the daemon already serializes create+restore, but
  // blocking re-tap here avoids a pointless duplicate and gives in-progress feedback.
  bool _restoring = false;
  bool _downloading = false;

  BackupSnapshot get snapshot => widget.snapshot;

  Future<void> _download() async {
    if (_downloading) return;
    final api = ref.read(backupApiProvider);
    if (api == null) {
      ScaffoldMessenger.of(context).showSnackBar(const SnackBar(content: Text('No server connected.')));
      return;
    }
    setState(() => _downloading = true);
    try {
      final uri = Uri.tryParse(api.absoluteDownloadUrl(snapshot));
      var ok = false;
      // Only ever launch http(s) — the URL is derived from a server-supplied path, so an
      // allowlist stops a rogue server steering us into a javascript:/data: scheme.
      if (uri != null && (uri.isScheme('http') || uri.isScheme('https'))) {
        try {
          ok = await launchUrl(uri, mode: LaunchMode.externalApplication);
        } catch (_) {
          // launchUrl can throw (e.g. PlatformException when no handler is registered);
          // swallow it and fall through to the snackbar.
          ok = false;
        }
      }
      if (!ok && mounted) {
        ScaffoldMessenger.of(context).showSnackBar(const SnackBar(content: Text('Could not open the download.')));
      }
    } finally {
      if (mounted) setState(() => _downloading = false);
    }
  }

  Future<void> _restore() async {
    if (_restoring) return;
    // Refuse if the snapshot has no area this client can restore — empty, OR only tokens
    // we don't recognise (e.g. a future daemon area). Otherwise the dialog would open with
    // no checkboxes and a permanently-disabled Restore button the user can't act on.
    final hasRestorable = snapshot.includedAreas.contains(BackupAreas.profiles) ||
        snapshot.includedAreas.contains(BackupAreas.sequences);
    if (!hasRestorable) {
      ScaffoldMessenger.of(context)
          .showSnackBar(const SnackBar(content: Text('This snapshot has no restorable areas.')));
      return;
    }
    if (ref.read(backupApiProvider) == null) {
      ScaffoldMessenger.of(context).showSnackBar(const SnackBar(content: Text('No server connected.')));
      return;
    }
    final choice = await showDialog<_RestoreChoice>(
      context: context,
      builder: (_) => _RestoreDialog(snapshot: snapshot),
    );
    if (choice == null || !mounted) return;
    setState(() => _restoring = true);
    try {
      final notifier = ref.read(backupSnapshotsProvider.notifier);
      await notifier.restore(
            snapshot,
            profiles: choice.profiles,
            sequences: choice.sequences,
          );
      // §43-2b: the restore runs on a background worker, so the 202 doesn't mean
      // it's done — poll clone-status to the real outcome (a worker-side failure
      // wouldn't surface from the POST otherwise). Cancel the poll if this modal
      // is dismissed mid-restore so it stops hitting the daemon.
      final status = await notifier.awaitRestoreTerminal(
        interval: widget.restorePollInterval,
        timeout: widget.restorePollTimeout,
        isCancelled: () => !mounted,
      );
      if (!mounted || status == null) return;
      if (status.isFailed) {
        ScaffoldMessenger.of(context).showSnackBar(
            SnackBar(content: Text('Restore failed: ${status.message ?? 'unknown error'}')));
      } else {
        ScaffoldMessenger.of(context)
            .showSnackBar(const SnackBar(content: Text('Restore complete. Reconnect equipment if needed.')));
      }
    } on TimeoutException {
      // The restore may still be running on the daemon — don't imply it failed.
      if (mounted) {
        ScaffoldMessenger.of(context).showSnackBar(const SnackBar(
            content: Text('Restore is taking longer than expected — check the server.')));
      }
    } catch (e) {
      if (mounted) {
        ScaffoldMessenger.of(context).showSnackBar(SnackBar(content: Text('Restore failed: ${_message(e)}')));
      }
    } finally {
      if (mounted) setState(() => _restoring = false);
    }
  }

  @override
  Widget build(BuildContext context) {
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
              onPressed: (_restoring || _downloading) ? null : () => unawaited(_download()),
            ),
            TextButton(
              onPressed: (_restoring || _downloading) ? null : () => unawaited(_restore()),
              child: _restoring
                  ? const SizedBox(width: 16, height: 16, child: CircularProgressIndicator(strokeWidth: 2))
                  : const Text('Restore'),
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

String _message(Object e) {
  // Friendly-ish text for the snackbar: HTTP errors carry a status; otherwise strip
  // the generic 'Exception: ' prefix. Avoids dumping a raw DioException toString().
  if (e is DioException) {
    // Don't surface DioException.message on the no-response path — it embeds the raw
    // request URL (host:port/path), which is noise in a snackbar.
    final code = e.response?.statusCode;
    return code != null ? 'server returned $code' : 'network error';
  }
  return e.toString().replaceFirst('Exception: ', '');
}

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
