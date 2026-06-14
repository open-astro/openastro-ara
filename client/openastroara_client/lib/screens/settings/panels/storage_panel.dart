import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../../services/profile_api.dart';
import '../../../state/saved_server_state.dart';
import '../../../state/settings/storage_settings_state.dart';
import '../../../theme/ara_colors.dart';
import '../../../widgets/backup/backup_restore_modal.dart';
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

class _StoragePanelState extends ConsumerState<StoragePanel> {
  bool _saving = false;
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

  Future<void> _save() async {
    final messenger = ScaffoldMessenger.of(context);
    // §29 — block an inverted disk-space pair before it reaches the daemon (the server also rejects it 400).
    if (!ref.read(storageSettingsProvider.notifier).thresholdsValid) {
      setState(() => _lastError =
          'Critical disk threshold must be below the warning threshold.');
      messenger.showSnackBar(SnackBar(content: Text(_lastError!)));
      return;
    }
    setState(() {
      _saving = true;
      _lastError = null;
    });
    final api = _api();
    if (api == null) {
      setState(() {
        _saving = false;
        _lastError = 'No active server — connect to a daemon first.';
      });
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
    } finally {
      if (mounted) setState(() => _saving = false);
    }
  }

  ProfileApi? _api() {
    final servers = ref.read(savedServersProvider).maybeWhen(
          data: (list) => list,
          orElse: () => const [],
        );
    if (servers.isEmpty) return null;
    // Most-recently-saved server is the de-facto active one — same
    // convention as §52.2 Alpaca chooser + §54 help dialog. Multi-server
    // active selection arrives with the §55.1 v0.1.0 roadmap.
    return ProfileApi(servers.last);
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
                'Storage is currently on the SD card. Capture > 1 night will '
                'wear the SD card out — connect a USB drive and click '
                '"Reformat as ext4" to migrate. (§29 + §29.1.3 wizard)',
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
              const Expanded(
                child: Text('12.3 GB / 32 GB  (real probe in 12h.2b)'),
              ),
            ],
          ),
        ),
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
        const SizedBox(height: 24),
        if (_lastError != null) ...[
          Text(
            _lastError!,
            style: TextStyle(color: Theme.of(context).colorScheme.error),
          ),
          const SizedBox(height: 12),
        ],
        Row(
          mainAxisAlignment: MainAxisAlignment.end,
          children: [
            FilledButton.icon(
              onPressed: _saving ? null : _save,
              icon: _saving
                  ? const SizedBox(
                      width: 14,
                      height: 14,
                      child: CircularProgressIndicator(strokeWidth: 2),
                    )
                  : const Icon(Icons.save, size: 16),
              label: Text(_saving ? 'Saving…' : 'Save'),
            ),
          ],
        ),
        const SizedBox(height: 24),
        const SettingsSectionHeader('Backup & Restore (§43)'),
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
