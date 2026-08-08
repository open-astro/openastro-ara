import 'package:flutter/material.dart';
import '../../../util/friendly_error.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../../services/profile_api.dart';
import '../../../state/saved_server_state.dart';
import '../../../state/settings/panel_save_registry.dart';
import '../../../state/settings/storage_settings_state.dart';
import '../../../services/storage_space_api.dart';
import '../../../theme/ara_colors.dart';
import '../../../services/storage_devices_api.dart';
import '../../../widgets/storage/server_folder_picker.dart';
import '../../../widgets/storage/storage_drive_dialog.dart';
import '../../../widgets/backup/backup_restore_modal.dart';
import '../../../state/backup/backup_stream_state.dart';
import '../../../widgets/settings/editable_field.dart';
import '../../../services/storage_browse_api.dart';
import '../../../widgets/help_icon.dart';
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
      if (mounted) {
        setState(
          () =>
              _lastError = friendlyError(e, action: 'load your saved settings'),
        );
      }
    }
  }

  @override
  Future<void> panelSave() => _save();

  Future<void> _save() async {
    final messenger = ScaffoldMessenger.of(context);
    // §29 — block an inverted disk-space pair before it reaches the daemon (the server also rejects it 400).
    if (!ref.read(storageSettingsProvider.notifier).thresholdsValid) {
      setState(
        () => _lastError =
            'Critical disk threshold must be below the warning threshold.',
      );
      messenger.showSnackBar(SnackBar(content: Text(_lastError!)));
      return;
    }
    setState(() => _lastError = null);
    final api = _api();
    if (api == null) {
      setState(
        () => _lastError = 'Not connected — connect to your rig to save this.',
      );
      messenger.showSnackBar(SnackBar(content: Text(_lastError!)));
      return;
    }
    try {
      await ref.read(storageSettingsProvider.notifier).persistToServer(api);
      if (!mounted) return;
      messenger.showSnackBar(const SnackBar(content: Text('Saved.')));
    } catch (e) {
      if (!mounted) return;
      setState(() => _lastError = friendlyError(e, action: 'save that'));
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
        // One destination, stated plainly, with the capacity you'd actually
        // ask about — the Time Machine "backup disk" model. Paths, mount
        // points and filesystems are plumbing; they live behind Advanced.
        const _DestinationCard(),
        ExpansionTile(
          title: Text(
            'Advanced',
            style: Theme.of(
              context,
            ).textTheme.bodyMedium?.copyWith(color: AraColors.textSecondary),
          ),
          tilePadding: EdgeInsets.zero,
          childrenPadding: const EdgeInsets.only(bottom: 8),
          children: [
            EditableTextRow(
              label: 'Save folder',
              helpKey: 'session.storage.save_directory',
              currentValue: s.saveDirectory,
              getCanonical: () =>
                  ref.read(storageSettingsProvider).saveDirectory,
              parse: n.setSaveDirectory,
            ),
            Align(
              alignment: Alignment.centerLeft,
              child: Padding(
                padding: const EdgeInsets.only(left: 280, top: 4),
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
                      startPath: s.saveDirectory.isEmpty
                          ? null
                          : s.saveDirectory,
                    );
                    if (picked != null) {
                      n.setSaveDirectory(picked);
                      ref.invalidate(storageSpaceProvider);
                    }
                  },
                  icon: const Icon(Icons.folder_open, size: 16),
                  label: const Text('Choose a folder…'),
                ),
              ),
            ),
          ],
        ),
        const SizedBox(height: 8),
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
        const SettingsRow(
          label: 'File naming',
          helpKey: 'session.filenames.always_written',
          value: 'Folders and filenames',
          hint: 'Edit in Your night → File naming',
        ),
        const SettingsSectionHeader('Warn me when space runs low'),
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
        const SettingsSectionHeader('Preview cache'),
        const _PreviewCacheRow(),
        const SettingsSectionHeader('Backups'),
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
        const SettingsSectionHeader(
          'Copy frames to this computer as they arrive',
        ),
        Consumer(
          builder: (context, ref, _) {
            final stream = ref.watch(backupStreamProvider);
            final n = ref.read(backupStreamProvider.notifier);
            return Column(
              crossAxisAlignment: CrossAxisAlignment.start,
              children: [
                SettingsSwitchRow(
                  label: 'Stream new frames to this device',
                  helpKey: 'session.storage.backup_stream',
                  value: stream.enabled,
                  hint:
                      'Mirror every captured FITS to this desktop as the night '
                      'runs — a dead imaging drive then costs at most the frame '
                      'being captured when it failed.',
                  onChanged: (v) => n.setEnabled(v),
                ),
                if (stream.enabled) ...[
                  EditableTextRow(
                    label: 'Backup folder',
                    helpKey: 'session.storage.backup_stream_folder',
                    currentValue: stream.localRoot,
                    getCanonical: () =>
                        ref.read(backupStreamProvider).localRoot,
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
          },
        ),
        const SizedBox(height: 24),
        const SettingsSectionHeader('Backup & Restore'),
        Padding(
          padding: const EdgeInsets.symmetric(vertical: 8),
          child: Text(
            'Back up your profile configuration (settings + sequences) to a ZIP '
            'snapshot on Ara, download a snapshot, or restore one.',
            style: Theme.of(
              context,
            ).textTheme.bodyMedium?.copyWith(color: AraColors.textSecondary),
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

/// §29 — where frames go, said the way a person would ask it: the drive's
/// name, how much room is left, and one way to change it. Everything else
/// (device paths, mount points, filesystems) lives behind Advanced or inside
/// the chooser, because it is plumbing.
class _DestinationCard extends ConsumerWidget {
  const _DestinationCard();

  static String _size(int bytes) {
    final gb = bytes / (1000 * 1000 * 1000);
    return gb >= 1000
        ? '${(gb / 1000).toStringAsFixed(2)} TB'
        : '${gb.toStringAsFixed(gb >= 10 ? 0 : 1)} GB';
  }

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final space = ref.watch(storageSpaceProvider);
    final devices = ref.watch(storageDevicesProvider);
    final theme = Theme.of(context);

    final current = devices.asData?.value
        .where((d) => d.isAraStore)
        .cast<StorageDevice?>()
        .firstWhere((_) => true, orElse: () => null);
    final onSystemDisk =
        devices.asData?.value.any((d) => d.isSystemDisk && d.isAraStore) ??
        false;

    final free = space.asData?.value?.freeBytes;
    final total = space.asData?.value?.totalBytes;
    final usedFraction = (free != null && total != null && total > 0)
        ? 1 - (free / total)
        : null;

    // Name it the way the user labelled it, not /dev/sdb1. friendlyName owns
    // the label→model→path fallback (including empty strings) — one place.
    final title =
        current?.friendlyName ??
        (onSystemDisk
            ? 'Internal storage'
            : (space.asData?.value?.isFallback ?? false)
            ? 'Server default folder'
            : 'Internal storage');

    final subtitle = space.when(
      loading: () => 'Reading…',
      error: (_, _) => 'Could not read the server\'s storage',
      data: (v) {
        if (v == null) {
          return 'Not connected to a server';
        }
        if (free == null || total == null) {
          return 'Unavailable — is the drive plugged in?';
        }
        return '${_size(free)} available of ${_size(total)}';
      },
    );

    return Container(
      margin: const EdgeInsets.only(bottom: 20),
      padding: const EdgeInsets.all(16),
      decoration: BoxDecoration(
        border: Border.all(
          color: AraColors.textSecondary.withValues(alpha: 0.35),
        ),
        borderRadius: BorderRadius.circular(10),
      ),
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          Row(
            children: [
              Icon(
                current != null ? Icons.usb : Icons.sd_card,
                size: 32,
                color: AraColors.textSecondary,
              ),
              const SizedBox(width: 14),
              Expanded(
                child: Column(
                  crossAxisAlignment: CrossAxisAlignment.start,
                  children: [
                    Text(
                      'Frames are saved to',
                      style: theme.textTheme.bodySmall?.copyWith(
                        color: AraColors.textSecondary,
                      ),
                    ),
                    const SizedBox(height: 2),
                    Text(
                      title,
                      style: theme.textTheme.titleMedium?.copyWith(
                        fontWeight: FontWeight.w600,
                      ),
                    ),
                    const SizedBox(height: 2),
                    Text(
                      subtitle,
                      style: theme.textTheme.bodySmall?.copyWith(
                        color: AraColors.textSecondary,
                      ),
                    ),
                  ],
                ),
              ),
              // "Look again at what's on this disk" — for the user who
              // plugs in a drive that already holds a season of frames.
              _RescanButton(),
              const SizedBox(width: 8),
              _CheckDiskButton(),
              const SizedBox(width: 8),
              _EjectButton(),
              const SizedBox(width: 8),
              FilledButton(
                onPressed: () => showStorageDriveDialog(context, ref),
                child: const Text('Change…'),
              ),
            ],
          ),
          if (usedFraction != null) ...[
            const SizedBox(height: 14),
            ClipRRect(
              borderRadius: BorderRadius.circular(4),
              child: LinearProgressIndicator(
                value: usedFraction.clamp(0.0, 1.0),
                minHeight: 8,
                backgroundColor: AraColors.textSecondary.withValues(alpha: 0.2),
              ),
            ),
          ],
          if (current == null && !(space.asData?.value == null)) ...[
            const SizedBox(height: 14),
            Row(
              crossAxisAlignment: CrossAxisAlignment.start,
              children: [
                const Icon(
                  Icons.info_outline,
                  size: 16,
                  color: AraColors.accentBusy,
                ),
                const SizedBox(width: 8),
                Expanded(
                  child: Text(
                    'Frames are going to the server\'s internal storage. '
                    'Sustained imaging wears out SD cards — choose an external '
                    'drive and it will be reconnected automatically after every '
                    'restart.',
                    style: theme.textTheme.bodySmall?.copyWith(
                      color: AraColors.textSecondary,
                    ),
                  ),
                ),
              ],
            ),
          ],
        ],
      ),
    );
  }
}

/// Runs the server-side §28.8 scan on demand and reports what it found in a
/// snackbar. Spins in place while the walk runs — a big library takes a
/// couple of seconds on a Pi.
/// §29 safe removal — flush + unmount before pulling the take-home drive.
/// Linux caches writes; yanking a mounted drive can lose the last seconds
/// of the night and leaves the exFAT dirty bit set.
class _EjectButton extends ConsumerStatefulWidget {
  @override
  ConsumerState<_EjectButton> createState() => _EjectButtonState();
}

class _EjectButtonState extends ConsumerState<_EjectButton> {
  bool _busy = false;

  Future<void> _run() async {
    final server = ref.read(activeServerProvider);
    if (server == null) return;
    final devices = ref.read(storageDevicesProvider).asData?.value;
    final store = devices
        ?.where((d) => d.isAraStore && d.uuid != null)
        .toList();
    final messenger = ScaffoldMessenger.of(context);
    if (store == null || store.isEmpty) {
      messenger.showSnackBar(
        const SnackBar(content: Text('No store drive to eject.')),
      );
      return;
    }
    setState(() => _busy = true);
    final api = StorageDevicesApi(server);
    try {
      final outcome = await api.eject(uuid: store.first.uuid!);
      if (!mounted) return;
      ref.invalidate(storageSpaceProvider);
      ref.invalidate(storageDevicesProvider);
      messenger.showSnackBar(
        SnackBar(
          content: Text(
            outcome.success
                ? 'Ejected — safe to unplug the drive.'
                : outcome.code == 'device_busy'
                ? 'The drive is busy — wait for writes to finish and try again.'
                : 'Eject failed: ${outcome.detail ?? outcome.code}',
          ),
        ),
      );
    } catch (e) {
      messenger.showSnackBar(
        SnackBar(content: Text(friendlyError(e, action: 'eject the drive'))),
      );
    } finally {
      api.close();
      if (mounted) setState(() => _busy = false);
    }
  }

  @override
  Widget build(BuildContext context) {
    return OutlinedButton.icon(
      onPressed: _busy ? null : _run,
      icon: _busy
          ? const SizedBox(
              width: 14,
              height: 14,
              child: CircularProgressIndicator(strokeWidth: 2),
            )
          : const Icon(Icons.eject, size: 16),
      label: const Text('Eject'),
    );
  }
}

/// §29 user-triggered filesystem check. exFAT (the take-home format) has no
/// journal, so after a field power cut this is how the drive gets healthy —
/// deliberately a button, never an automatic scan.
class _CheckDiskButton extends ConsumerStatefulWidget {
  @override
  ConsumerState<_CheckDiskButton> createState() => _CheckDiskButtonState();
}

class _CheckDiskButtonState extends ConsumerState<_CheckDiskButton> {
  bool _busy = false;

  Future<void> _run() async {
    final server = ref.read(activeServerProvider);
    if (server == null) return;
    final devices = ref.read(storageDevicesProvider).asData?.value;
    final store = devices
        ?.where((d) => d.isAraStore && d.uuid != null)
        .toList();
    final messenger = ScaffoldMessenger.of(context);
    if (store == null || store.isEmpty) {
      messenger.showSnackBar(
        const SnackBar(
          content: Text('No store drive to check — connect one first.'),
        ),
      );
      return;
    }
    final proceed = await showDialog<bool>(
      context: context,
      builder: (context) => AlertDialog(
        title: const Text('Check the store drive?'),
        content: const Text(
          'The drive is briefly taken offline while its filesystem is '
          'verified and repaired if needed. Takes seconds to a couple of '
          'minutes; capture must be idle.',
        ),
        actions: [
          TextButton(
            onPressed: () => Navigator.of(context).pop(false),
            child: const Text('Cancel'),
          ),
          FilledButton(
            onPressed: () => Navigator.of(context).pop(true),
            child: const Text('Check disk'),
          ),
        ],
      ),
    );
    if (proceed != true || !mounted) return;
    setState(() => _busy = true);
    final api = StorageDevicesApi(server);
    try {
      final outcome = await api.check(uuid: store.first.uuid!);
      if (!mounted) return;
      ref.invalidate(storageSpaceProvider);
      messenger.showSnackBar(
        SnackBar(
          content: Text(
            outcome.success
                ? (outcome.code == 'repaired'
                      ? 'Disk checked — repairs were made. Worth re-running '
                            '"Find frames on disk".'
                      : 'Disk checked — clean.')
                : 'Check failed: ${outcome.detail ?? outcome.code}',
          ),
        ),
      );
    } catch (e) {
      messenger.showSnackBar(
        SnackBar(content: Text(friendlyError(e, action: 'check the disk'))),
      );
    } finally {
      api.close();
      if (mounted) setState(() => _busy = false);
    }
  }

  @override
  Widget build(BuildContext context) {
    return OutlinedButton(
      onPressed: _busy ? null : _run,
      child: _busy
          ? const SizedBox(
              width: 14,
              height: 14,
              child: CircularProgressIndicator(strokeWidth: 2),
            )
          : const Text('Check disk'),
    );
  }
}

class _RescanButton extends ConsumerStatefulWidget {
  @override
  ConsumerState<_RescanButton> createState() => _RescanButtonState();
}

class _RescanButtonState extends ConsumerState<_RescanButton> {
  bool _busy = false;

  Future<void> _run() async {
    final server = ref.read(activeServerProvider);
    if (server == null) return;
    setState(() => _busy = true);
    final messenger = ScaffoldMessenger.of(context);
    final api = StorageDevicesApi(server);
    try {
      final outcome = await api.rescan();
      if (!mounted) return;
      if (outcome.ran && outcome.framesRecovered > 0) {
        // The library just changed — anything watching space/devices should
        // reflect the recovered frames.
        ref.invalidate(storageSpaceProvider);
      }
      messenger.showSnackBar(SnackBar(content: Text(outcome.message)));
    } catch (e) {
      messenger.showSnackBar(
        SnackBar(
          content: Text(friendlyError(e, action: 'check the disk for frames')),
        ),
      );
    } finally {
      api.close();
      if (mounted) setState(() => _busy = false);
    }
  }

  @override
  Widget build(BuildContext context) {
    return OutlinedButton(
      onPressed: _busy ? null : _run,
      child: _busy
          ? const SizedBox(
              width: 14,
              height: 14,
              child: CircularProgressIndicator(strokeWidth: 2),
            )
          : const Text('Find frames on disk'),
    );
  }
}

/// §65.4 preview-cache size, live from the server, so "Clean cache" shows
/// what it would actually free before the user commits.
final storageCacheProvider = FutureProvider.autoDispose<StorageCacheInfo?>((
  ref,
) async {
  final server = ref.watch(activeServerProvider);
  if (server == null) return null;
  final api = StorageBrowseApi(server);
  ref.onDispose(api.close);
  return api.fetchCacheInfo();
});

/// "Preview cache" row — current size + a Clean cache button, with the §69
/// ⓘ help (hover for the short version, tap for the full story).
class _PreviewCacheRow extends ConsumerStatefulWidget {
  const _PreviewCacheRow();

  @override
  ConsumerState<_PreviewCacheRow> createState() => _PreviewCacheRowState();
}

class _PreviewCacheRowState extends ConsumerState<_PreviewCacheRow> {
  bool _cleaning = false;

  String _size(StorageCacheInfo info) {
    final mb = info.bytes / 1e6;
    final size = mb >= 1000
        ? '${(mb / 1000).toStringAsFixed(2)} GB'
        : '${mb.toStringAsFixed(0)} MB';
    return '$size · ${info.files} file${info.files == 1 ? '' : 's'}';
  }

  Future<void> _clean() async {
    final server = ref.read(activeServerProvider);
    if (server == null) return;
    final messenger = ScaffoldMessenger.of(context);
    setState(() => _cleaning = true);
    final api = StorageBrowseApi(server);
    try {
      final freed = await api.clearCache();
      if (!mounted) return;
      messenger.showSnackBar(
        SnackBar(
          content: Text(
            freed.files == 0
                ? 'Preview cache was already empty.'
                : 'Cleaned the preview cache — freed ${_size(freed)}.',
          ),
        ),
      );
    } catch (e) {
      if (!mounted) return;
      messenger.showSnackBar(
        SnackBar(
          content: Text(friendlyError(e, action: 'clean the preview cache')),
        ),
      );
    } finally {
      api.close();
      if (mounted) setState(() => _cleaning = false);
      ref.invalidate(storageCacheProvider);
    }
  }

  @override
  Widget build(BuildContext context) {
    final cache = ref.watch(storageCacheProvider);
    final info = cache.asData?.value;
    final String sizeText;
    if (cache.isLoading) {
      sizeText = 'Measuring…';
    } else if (info == null) {
      sizeText = cache.hasError ? 'Size unavailable' : 'Connect to a server';
    } else if (info.files == 0) {
      sizeText = 'Empty';
    } else {
      sizeText = _size(info);
    }
    return Padding(
      padding: const EdgeInsets.symmetric(vertical: 8),
      child: Row(
        children: [
          SizedBox(
            width: 280,
            child: Row(
              children: [
                Text(
                  'Thumbnails + previews',
                  style: Theme.of(context).textTheme.bodyMedium?.copyWith(
                    color: AraColors.textSecondary,
                  ),
                ),
                const HelpIcon(helpKey: 'session.storage.preview_cache'),
              ],
            ),
          ),
          Expanded(child: Text(sizeText)),
          OutlinedButton.icon(
            onPressed: _cleaning || info == null || info.files == 0
                ? null
                : _clean,
            icon: _cleaning
                ? const SizedBox(
                    width: 14,
                    height: 14,
                    child: CircularProgressIndicator(strokeWidth: 2),
                  )
                : const Icon(Icons.cleaning_services_outlined, size: 16),
            label: Text(_cleaning ? 'Cleaning…' : 'Clean cache'),
          ),
        ],
      ),
    );
  }
}
