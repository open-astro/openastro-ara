import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../../services/profile_api.dart';
import '../../../state/saved_server_state.dart';
import '../../../state/settings/panel_save_registry.dart';
import '../../../state/settings/storage_settings_state.dart';
import '../../../services/storage_space_api.dart';
import '../../../theme/ara_colors.dart';
import '../../../widgets/storage/server_folder_picker.dart';
import '../../../widgets/storage/storage_drive_dialog.dart';
import '../../../widgets/backup/backup_restore_modal.dart';
import '../../../state/backup/backup_stream_state.dart';
import '../../../widgets/settings/editable_field.dart';
import '../../../widgets/settings/settings_row.dart';

/// Storage panel per §29 — save directory + format + compression + filename
/// template. Phase 12h.6c added the daemon round-trip — values hydrate from
/// the active server on mount and persist back on Save.
class StoragePanel extends ConsumerStatefulWidget {
  const StoragePanel({super.key});

  @override
  ConsumerState<StoragePanel> createState() => _StoragePanelState();
}

class _StoragePanelState extends ConsumerState<StoragePanel>
    with PanelSaveRegistration {
  String? _lastError;

  @override
  void initState() {
    super.initState();
    WidgetsBinding.instance.addPostFrameCallback((_) => _hydrate());
  }

  Future<void> _hydrate() async {
    final api = _api();
    if (api == null) return;
    try {
      await ref.read(storageSettingsProvider.notifier).hydrateFromServer(api);
    } catch (e) {
      if (mounted) setState(() => _lastError = 'Could not load saved values: $e');
    }
  }

  @override
  Future<void> panelSave() => _save();

  Future<void> _save() async {
    final messenger = ScaffoldMessenger.of(context);
    // §29 — block an inverted disk-space pair before it reaches the daemon (the server also rejects it 400).
    if (!ref.read(storageSettingsProvider.notifier).thresholdsValid) {
      setState(() => _lastError =
          'Critical disk threshold must be below the warning threshold.');
      messenger.showSnackBar(SnackBar(content: Text(_lastError!)));
      return;
    }
    setState(() => _lastError = null);
    final api = _api();
    if (api == null) {
      setState(
          () => _lastError = 'No active server — connect to a daemon first.');
      messenger.showSnackBar(SnackBar(content: Text(_lastError!)));
      return;
    }
    try {
      await ref.read(storageSettingsProvider.notifier).persistToServer(api);
      if (!mounted) return;
      messenger.showSnackBar(
        const SnackBar(content: Text('Storage settings saved to daemon.')),
      );
    } catch (e) {
      if (!mounted) return;
      setState(() => _lastError = 'Save failed: $e');
      messenger.showSnackBar(SnackBar(content: Text(_lastError!)));
    }
  }

  ProfileApi? _api() {
    final server = ref.read(activeServerProvider);
    return server == null ? null : ProfileApi(server);
  }

  @override
  Widget build(BuildContext context) {
    final s = ref.watch(storageSettingsProvider);
    final n = ref.read(storageSettingsProvider.notifier);
    return ListView(
      padding: const EdgeInsets.all(24),
      children: [
        Container(
          padding: const EdgeInsets.all(12),
          decoration: BoxDecoration(
            color: AraColors.accentBusy.withValues(alpha: 0.12),
            border: Border.all(color: AraColors.accentBusy),
            borderRadius: BorderRadius.circular(4),
          ),
          child: Row(children: const [
            Icon(Icons.warning_amber, size: 18, color: AraColors.accentBusy),
            SizedBox(width: 8),
            Expanded(
              child: Text(
                'Storage is on the SD card. Sustained capture wears SD cards '
                'out — use "Choose drive…" below to move saving to a USB '
                'drive or SSD (it is remounted automatically on every boot).',
              ),
            ),
          ]),
        ),
        const SizedBox(height: 16),
        EditableTextRow(
          label: 'Save directory',
          helpKey: 'session.storage.save_directory',
          currentValue: s.saveDirectory,
          getCanonical: () => ref.read(storageSettingsProvider).saveDirectory,
          parse: n.setSaveDirectory,
        ),
        Padding(
          padding: const EdgeInsets.only(left: 280, bottom: 8),
          child: Align(
            alignment: Alignment.centerLeft,
            child: OutlinedButton.icon(
              onPressed: () async {
                final server = ref.read(activeServerProvider);
                if (server == null) {
                  return;
                }
                final picked = await showServerFolderPicker(
                  context,
                  ref,
                  server: server,
                  startPath: s.saveDirectory.isEmpty ? null : s.saveDirectory,
                );
                if (picked != null) {
                  n.setSaveDirectory(picked);
                  ref.invalidate(storageSpaceProvider);
                }
              },
              icon: const Icon(Icons.folder_open, size: 16),
              label: const Text('Browse the server…'),
            ),
          ),
        ),
        Padding(
          padding: const EdgeInsets.only(left: 280, bottom: 8),
          child: Align(
            alignment: Alignment.centerLeft,
            child: OutlinedButton.icon(
              onPressed: () => showStorageDriveDialog(context, ref),
              icon: const Icon(Icons.usb, size: 16),
              label: const Text('Choose drive…'),
            ),
          ),
        ),
        const _FreeSpaceRow(),
        SettingsDropdownRow<StorageFileFormat>(
          label: 'File format',
          helpKey: 'session.storage.file_format',
          value: s.fileFormat,
          items: const {
            StorageFileFormat.fits: 'FITS',
            StorageFileFormat.xisf: 'XISF',
            StorageFileFormat.fitsRice: 'FITS + RICE compression',
            StorageFileFormat.fitsGzip: 'FITS + gzip',
          },
          onChanged: (v) {
            if (v != null) n.setFileFormat(v);
          },
        ),
        SettingsDropdownRow<StorageCompression>(
          label: 'Compression',
          helpKey: 'session.storage.compression',
          value: s.compression,
          items: const {
            StorageCompression.off: 'Off',
            StorageCompression.rice: 'RICE',
            StorageCompression.gzip: 'gzip',
          },
          onChanged: (v) {
            if (v != null) n.setCompression(v);
          },
        ),
        EditableTextRow(
          label: 'Filename template',
          helpKey: 'session.storage.filename_template',
          currentValue: s.filenameTemplate,
          getCanonical: () =>
              ref.read(storageSettingsProvider).filenameTemplate,
          parse: n.setFilenameTemplate,
          maxLines: 2,
        ),
        const SettingsSectionHeader('Low-disk-space warning (§29)'),
        EditableNumberRow(
          label: 'Warn below (GB free)',
          helpKey: 'session.storage.min_free_disk_warn_gb',
          currentValue: s.minFreeDiskWarnGb.toString(),
          getCanonical: () =>
              ref.read(storageSettingsProvider).minFreeDiskWarnGb.toString(),
          parse: (v) {
            final gb = int.tryParse(v.trim());
            if (gb != null) n.setMinFreeDiskWarnGb(gb);
          },
        ),
        EditableNumberRow(
          label: 'Critical below (GB free)',
          helpKey: 'session.storage.min_free_disk_critical_gb',
          currentValue: s.minFreeDiskCriticalGb.toString(),
          getCanonical: () => ref
              .read(storageSettingsProvider)
              .minFreeDiskCriticalGb
              .toString(),
          parse: (v) {
            final gb = int.tryParse(v.trim());
            if (gb != null) n.setMinFreeDiskCriticalGb(gb);
          },
        ),
        const SettingsSectionHeader('Backups (§43)'),
        EditableNumberRow(
          label: 'Keep backup snapshots',
          helpKey: 'session.storage.backup_retention_count',
          currentValue: s.backupRetentionCount.toString(),
          getCanonical: () =>
              ref.read(storageSettingsProvider).backupRetentionCount.toString(),
          parse: (v) {
            final count = int.tryParse(v.trim());
            if (count != null) n.setBackupRetentionCount(count);
          },
        ),
        const SizedBox(height: 24),
        if (_lastError != null) ...[
          Text(
            _lastError!,
            style: TextStyle(color: Theme.of(context).colorScheme.error),
          ),
          const SizedBox(height: 12),
        ],
        // Save lives in the settings-shell header (PanelSaveRegistration) —
        // fixed chrome, always visible, no scrolling to find it.
        const SizedBox(height: 24),
        const SettingsSectionHeader('Real-time frame backup (§44)'),
        Consumer(builder: (context, ref, _) {
          final stream = ref.watch(backupStreamProvider);
          final n = ref.read(backupStreamProvider.notifier);
          return Column(
            crossAxisAlignment: CrossAxisAlignment.start,
            children: [
              SettingsSwitchRow(
                label: 'Stream new frames to this device',
                helpKey: 'session.storage.backup_stream',
                value: stream.enabled,
                hint: 'Mirror every captured FITS to this desktop as the night '
                    'runs — a dead imaging drive then costs at most the frame '
                    'being captured when it failed.',
                onChanged: (v) => n.setEnabled(v),
              ),
              if (stream.enabled) ...[
                EditableTextRow(
                  label: 'Backup folder',
                  helpKey: 'session.storage.backup_stream_folder',
                  currentValue: stream.localRoot,
                  getCanonical: () => ref.read(backupStreamProvider).localRoot,
                  parse: n.setLocalRoot,
                ),
                EditableTextRow(
                  label: 'Bandwidth cap (Mbps, 0 = unlimited)',
                  helpKey: 'session.storage.backup_stream_mbps',
                  currentValue: stream.maxMbps.toString(),
                  getCanonical: () =>
                      ref.read(backupStreamProvider).maxMbps.toString(),
                  parse: (str) {
                    final v = int.tryParse(str);
                    if (v != null) n.setMaxMbps(v);
                  },
                ),
              ],
              // The problem line renders even when disabled — an auto-disable
              // (another desktop took the slot) must explain itself, not just
              // silently flip the toggle off.
              if (stream.enabled || stream.problem != null)
                Padding(
                  padding: const EdgeInsets.symmetric(vertical: 6),
                  child: Text(
                    stream.problem ??
                        (stream.active
                            ? 'Streaming — ${stream.pendingCount} pending, '
                                '${stream.syncedThisSession} synced this session '
                                '(${(stream.syncedBytesThisSession / (1024 * 1024)).toStringAsFixed(1)} MB)'
                                '${stream.measuredMbps != null ? ', link ≈ ${stream.measuredMbps!.toStringAsFixed(0)} Mbps' : ''}.'
                            : 'Starting…'),
                    style: TextStyle(
                      color: stream.problem != null
                          ? Theme.of(context).colorScheme.error
                          : Theme.of(context).textTheme.bodySmall?.color,
                      fontSize: 12,
                    ),
                  ),
                ),
            ],
          );
        }),
        const SizedBox(height: 24),
        const SettingsSectionHeader('Backup & Restore'),
        Padding(
          padding: const EdgeInsets.symmetric(vertical: 8),
          child: Text(
            'Back up your profile configuration (settings + sequences) to a ZIP '
            'snapshot on the daemon, download a snapshot, or restore one.',
            style: Theme.of(context).textTheme.bodyMedium?.copyWith(color: AraColors.textSecondary),
          ),
        ),
        Align(
          alignment: Alignment.centerLeft,
          child: FilledButton.icon(
            onPressed: () => showDialog<void>(
              context: context,
              builder: (_) => const BackupRestoreModal(),
            ),
            icon: const Icon(Icons.backup, size: 16),
            label: const Text('Open Backup & Restore'),
          ),
        ),
      ],
    );
  }
}


/// §29 — the real volume behind the save directory. Renders "unavailable" (not
/// a number) when the daemon can't read it, e.g. an unmounted USB store.
class _FreeSpaceRow extends ConsumerWidget {
  const _FreeSpaceRow();

  static String _gb(int bytes) => (bytes / (1000 * 1000 * 1000)).toStringAsFixed(1);

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final async = ref.watch(storageSpaceProvider);
    final text = async.when(
      loading: () => 'Reading…',
      error: (_, _) => 'Unavailable (the daemon could not read the volume)',
      data: (space) {
        if (space == null) {
          return 'Not connected to a server';
        }
        final free = space.freeBytes;
        final total = space.totalBytes;
        if (free == null || total == null) {
          return 'Unavailable — is ${space.saveDirectory} mounted?';
        }
        final pct = total > 0 ? (100 * free / total).round() : 0;
        final where = space.isFallback ? ' (daemon fallback directory)' : '';
        return '${_gb(free)} GB free of ${_gb(total)} GB ($pct%)$where';
      },
    );
    return Padding(
      padding: const EdgeInsets.symmetric(vertical: 8),
      child: Row(
        children: [
          SizedBox(
            width: 280,
            child: Text('Free space',
                style: Theme.of(context).textTheme.bodyMedium?.copyWith(
                      color: AraColors.textSecondary,
                    )),
          ),
          Expanded(child: Text(text)),
          IconButton(
            tooltip: 'Re-check',
            iconSize: 18,
            onPressed: () => ref.invalidate(storageSpaceProvider),
            icon: const Icon(Icons.refresh),
          ),
        ],
      ),
    );
  }
}
