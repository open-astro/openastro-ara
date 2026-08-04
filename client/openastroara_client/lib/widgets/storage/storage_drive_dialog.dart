import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../services/storage_devices_api.dart';
import '../../services/storage_space_api.dart';
import '../../state/saved_server_state.dart';
import '../../state/settings/storage_settings_state.dart';
import '../../theme/ara_colors.dart';

/// §29.1.1/§29.1.3 — pick the drive ARA saves to. Lists the server's block
/// devices (system disk excluded), mounts an ext4 drive straight away, and
/// offers the destructive reformat path for anything else. The mount is
/// written to fstab server-side, so it survives reboots — the reason a drive
/// picked here stays picked.
Future<void> showStorageDriveDialog(BuildContext context, WidgetRef ref) async {
  final server = ref.read(activeServerProvider);
  if (server == null) {
    return;
  }
  await showDialog<void>(
    context: context,
    builder: (_) => const _StorageDriveDialog(),
  );
  ref.invalidate(storageSpaceProvider);
  ref.invalidate(storageDevicesProvider);
}

class _StorageDriveDialog extends ConsumerStatefulWidget {
  const _StorageDriveDialog();

  @override
  ConsumerState<_StorageDriveDialog> createState() => _StorageDriveDialogState();
}

class _StorageDriveDialogState extends ConsumerState<_StorageDriveDialog> {
  bool _busy = false;
  String? _status;

  Future<void> _use(StorageDevice device) async {
    final server = ref.read(activeServerProvider);
    if (server == null) {
      return;
    }
    // Non-ext4 (or blank) drives can't just be mounted — confirm the erase.
    if (!device.isExt4) {
      final confirmed = await _confirmReformat(device);
      if (confirmed != true) {
        return;
      }
    }
    setState(() {
      _busy = true;
      _status = device.isExt4
          ? 'Mounting ${device.displayName}…'
          : 'Formatting ${device.displayName} as ext4 — this can take a minute…';
    });
    final api = StorageDevicesApi(server);
    try {
      final outcome = await api.configure(
        uuid: device.uuid ?? '',
        format: !device.isExt4,
        confirmLabel: device.isExt4 ? null : (device.label ?? ''),
      );
      if (!mounted) {
        return;
      }
      if (outcome.success) {
        // Point the panel's save-directory field at the new mount so the
        // user sees the change without hunting for it.
        final dir = outcome.saveDirectory;
        if (dir != null && dir.isNotEmpty) {
          ref.read(storageSettingsProvider.notifier).setSaveDirectory(dir);
        }
        Navigator.of(context).pop();
        ScaffoldMessenger.of(context).showSnackBar(
          SnackBar(content: Text('Saving to ${dir ?? '/media/openastroara'}')),
        );
        return;
      }
      setState(() => _status = _friendly(outcome, device));
    } finally {
      api.close();
      if (mounted) {
        setState(() => _busy = false);
      }
    }
  }

  static String _friendly(StorageConfigureOutcome outcome, StorageDevice device) =>
      switch (outcome.code) {
        'not_ext4' =>
          'That drive is ${outcome.detail ?? 'not ext4'} — it must be reformatted first.',
        'label_mismatch' =>
          'The drive label changed (now ${outcome.detail ?? 'unknown'}) — re-scan and try again.',
        'device_busy' =>
          'The drive is in use and could not be unmounted. Stop anything reading it and retry.',
        'system_disk' || 'refused' =>
          'Refused: that device carries the running system.',
        'uuid_not_found' =>
          'The drive disappeared — re-plug it, then Re-scan.',
        'helper_missing' =>
          'The server is missing its storage helper script. Reinstall the openastroara-server package.',
        'mkfs_failed' => 'Formatting failed. Check the daemon log for details.',
        'mount_failed' => 'The drive was prepared but would not mount.',
        _ => outcome.detail ?? 'Could not configure ${device.path} (${outcome.code}).',
      };

  Future<bool?> _confirmReformat(StorageDevice device) {
    final controller = TextEditingController();
    final label = device.label ?? '';
    return showDialog<bool>(
      context: context,
      builder: (ctx) => StatefulBuilder(
        builder: (ctx, setLocal) => AlertDialog(
          title: const Text('Erase and reformat this drive?'),
          content: Column(
            mainAxisSize: MainAxisSize.min,
            crossAxisAlignment: CrossAxisAlignment.start,
            children: [
              Text('Drive: ${device.displayName}'),
              Text('Size: ${device.sizeText}'),
              Text('Current filesystem: ${device.fileSystem ?? 'none'}'),
              const SizedBox(height: 12),
              const Text(
                'ARA stores frames on ext4 for the durability its capture '
                'pipeline depends on. Reformatting will PERMANENTLY ERASE '
                'everything on this drive.',
              ),
              const SizedBox(height: 12),
              if (label.isEmpty)
                const Text('This drive has no label, so no confirmation text '
                    'is required — check the size and path above carefully.')
              else ...[
                Text('Type the drive label "$label" to confirm '
                    '(case-sensitive):'),
                TextField(
                  controller: controller,
                  autofocus: true,
                  onChanged: (_) => setLocal(() {}),
                  decoration: const InputDecoration(isDense: true),
                ),
              ],
            ],
          ),
          actions: [
            TextButton(
                onPressed: () => Navigator.of(ctx).pop(false),
                child: const Text('Cancel')),
            FilledButton(
              style: FilledButton.styleFrom(backgroundColor: AraColors.accentBusy),
              onPressed: label.isEmpty || controller.text == label
                  ? () => Navigator.of(ctx).pop(true)
                  : null,
              child: const Text('Erase and use this drive'),
            ),
          ],
        ),
      ),
    ).whenComplete(controller.dispose);
  }

  @override
  Widget build(BuildContext context) {
    final async = ref.watch(storageDevicesProvider);
    return AlertDialog(
      title: const Text('Choose the drive ARA saves to'),
      content: SizedBox(
        width: 560,
        child: async.when(
          loading: () => const SizedBox(
              height: 120, child: Center(child: CircularProgressIndicator())),
          error: (e, _) => SizedBox(
              height: 120, child: Center(child: Text('Could not read drives: $e'))),
          data: (devices) {
            final usable = devices.where((d) => !d.isSystemDisk).toList();
            if (usable.isEmpty) {
              return const SizedBox(
                height: 120,
                child: Center(
                  child: Text('No usable drives found. Plug in a USB drive '
                      'and press Re-scan.'),
                ),
              );
            }
            return Column(
              mainAxisSize: MainAxisSize.min,
              children: [
                if (_status != null)
                  Padding(
                    padding: const EdgeInsets.only(bottom: 8),
                    child: Text(_status!,
                        style: const TextStyle(color: AraColors.accentBusy)),
                  ),
                Flexible(
                  child: ListView.separated(
                    shrinkWrap: true,
                    itemCount: usable.length,
                    separatorBuilder: (_, _) => const Divider(height: 1),
                    itemBuilder: (_, i) {
                      final d = usable[i];
                      final subtitle = [
                        d.sizeText,
                        d.fileSystem ?? 'no filesystem',
                        if (d.mountPoint != null) 'mounted at ${d.mountPoint}',
                        if (d.transport != null) d.transport!,
                      ].where((p) => p.isNotEmpty).join(' · ');
                      return ListTile(
                        enabled: !_busy && d.selectable,
                        leading: Icon(d.isAraStore
                            ? Icons.check_circle
                            : (d.removable ? Icons.usb : Icons.storage)),
                        title: Text(d.displayName),
                        subtitle: Text(subtitle),
                        trailing: d.isAraStore
                            ? const Text('current')
                            : Text(d.isExt4 ? 'Use' : 'Format + use'),
                        onTap: _busy || !d.selectable ? null : () => _use(d),
                      );
                    },
                  ),
                ),
              ],
            );
          },
        ),
      ),
      actions: [
        TextButton(
          onPressed: _busy
              ? null
              : () => ref.invalidate(storageDevicesProvider),
          child: const Text('Re-scan'),
        ),
        TextButton(
          onPressed: _busy ? null : () => Navigator.of(context).pop(),
          child: const Text('Close'),
        ),
      ],
    );
  }
}
