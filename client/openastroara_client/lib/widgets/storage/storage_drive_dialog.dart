import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../services/storage_devices_api.dart';
import '../../services/storage_space_api.dart';
import '../../state/saved_server_state.dart';
import '../../state/settings/storage_settings_state.dart';
import '../../theme/ara_colors.dart';

/// §29.1.1/§29.1.3 — "choose a disk for your frames", after the Time Machine
/// model: one list of disks, select, confirm. Erasing (for disks ARA can't use
/// as-is) is a step *inside* this flow, not a separate concept the user has to
/// understand up front. Device paths and filesystem names appear only as
/// secondary detail.
Future<void> showStorageDriveDialog(BuildContext context, WidgetRef ref) async {
  if (ref.read(activeServerProvider) == null) {
    return;
  }
  await showDialog<void>(
    context: context,
    builder: (_) => const _DiskChooser(),
  );
  // The caller's element can be gone by the time the dialog closes —
  // touching its ref then throws (the classic Riverpod after-await footgun).
  if (!context.mounted) return;
  ref.invalidate(storageSpaceProvider);
  ref.invalidate(storageDevicesProvider);
}

class _DiskChooser extends ConsumerStatefulWidget {
  const _DiskChooser();

  @override
  ConsumerState<_DiskChooser> createState() => _DiskChooserState();
}

class _DiskChooserState extends ConsumerState<_DiskChooser> {
  StorageDevice? _selected;
  final TextEditingController _confirm = TextEditingController();
  bool _busy = false;
  String? _error;

  @override
  void dispose() {
    _confirm.dispose();
    super.dispose();
  }

  bool get _needsErase => _selected != null && !_selected!.isMountable;

  /// Format choice for the erase path. exFAT is the default — the drive
  /// plugs straight into Windows/macOS at home (the §29 field workflow);
  /// ext4 is the rig-resident choice.
  String _newFilesystem = 'exfat';

  /// A labelled disk asks the user to type its name — the standard guard
  /// against erasing the wrong one. An unlabelled disk still holds someone's
  /// data (stock USB sticks ship label-less), so it demands typing ERASE:
  /// a destructive click is never one static warning away.
  bool get _confirmSatisfied {
    if (!_needsErase) {
      return true;
    }
    final label = _selected!.label ?? '';
    return label.isEmpty ? _confirm.text == 'ERASE' : _confirm.text == label;
  }

  Future<void> _useDisk() async {
    final device = _selected;
    final server = ref.read(activeServerProvider);
    if (device == null || server == null) {
      return;
    }
    setState(() {
      _busy = true;
      _error = null;
    });
    final api = StorageDevicesApi(server);
    try {
      final outcome = await api.configure(
        // A brand-new blank disk has no filesystem yet, hence no UUID — the
        // server accepts its /dev/ path for exactly this onboarding case.
        uuid: device.uuid ?? device.path,
        format: _needsErase,
        // What the user actually typed — the server re-checks it against the
        // drive's real label, so a stale client record can't erase the wrong
        // disk. An unlabelled drive's typed ERASE is a client-side bar only;
        // the server must see the empty label the drive actually has.
        confirmLabel: _needsErase
            ? ((device.label ?? '').isEmpty ? '' : _confirm.text)
            : null,
        filesystem: _needsErase ? _newFilesystem : null,
      );
      if (!mounted) {
        return;
      }
      if (outcome.success) {
        final dir = outcome.saveDirectory;
        if (dir != null && dir.isNotEmpty) {
          ref.read(storageSettingsProvider.notifier).setSaveDirectory(dir);
        }
        Navigator.of(context).pop();
        ScaffoldMessenger.of(context).showSnackBar(SnackBar(
          content: Text('${device.friendlyName} is now used for frames.'),
        ));
        return;
      }
      setState(() => _error = _friendly(outcome, device));
    } finally {
      api.close();
      if (mounted) {
        setState(() => _busy = false);
      }
    }
  }

  static String _friendly(StorageConfigureOutcome outcome, StorageDevice device) =>
      switch (outcome.code) {
        'not_ext4' => 'This disk needs to be erased before ARA can use it.',
        'label_mismatch' =>
          'The disk changed since the list was loaded. Rescan and try again.',
        'device_busy' =>
          'The disk is in use. Close anything reading it, then try again.',
        'system_disk' || 'refused' =>
          'That disk holds the server\'s operating system and can\'t be used.',
        'uuid_not_found' => 'The disk disconnected. Reconnect it and rescan.',
        'helper_missing' =>
          'The server is missing part of its installation. Reinstall openastroara-server.',
        'mkfs_failed' => 'Erasing the disk failed. See the server log for details.',
        'mount_failed' => 'The disk was prepared but could not be connected.',
        _ => outcome.detail ?? 'Could not use ${device.friendlyName}.',
      };

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    final async = ref.watch(storageDevicesProvider);
    return AlertDialog(
      title: const Text('Choose a disk for your frames'),
      content: SizedBox(
        width: 520,
        child: async.when(
          loading: () => const SizedBox(
              height: 140, child: Center(child: CircularProgressIndicator())),
          error: (e, _) => SizedBox(
            height: 140,
            child: Center(child: Text('Could not read the server\'s disks.\n$e',
                textAlign: TextAlign.center)),
          ),
          data: (all) {
            final disks = all.where((d) => !d.isSystemDisk).toList();
            // A rescan hands out fresh device objects — re-point the
            // selection at its current record so the erase/label logic never
            // reads pre-rescan state. A disk that vanished deselects.
            final selectedPath = _selected?.path;
            if (selectedPath != null) {
              _selected = null;
              for (final d in disks) {
                if (d.path == selectedPath) {
                  _selected = d;
                  break;
                }
              }
            }
            if (disks.isEmpty) {
              return const SizedBox(
                height: 140,
                child: Center(
                  child: Text(
                    'No external disks found.\n\n'
                    'Connect a USB drive or SSD to the server, then Rescan.',
                    textAlign: TextAlign.center,
                  ),
                ),
              );
            }
            return Column(
              mainAxisSize: MainAxisSize.min,
              crossAxisAlignment: CrossAxisAlignment.start,
              children: [
                Flexible(
                  child: ListView.builder(
                    shrinkWrap: true,
                    itemCount: disks.length,
                    itemBuilder: (_, i) {
                      final d = disks[i];
                      final selected = identical(d, _selected) ||
                          (_selected != null && d.path == _selected!.path);
                      return ListTile(
                        selected: selected,
                        selectedTileColor:
                            AraColors.textSecondary.withValues(alpha: 0.12),
                        leading: Icon(d.removable ? Icons.usb : Icons.storage,
                            size: 28),
                        title: Text(d.friendlyName),
                        subtitle: Text(d.detailLine),
                        trailing: d.isAraStore
                            ? Text('In use', style: theme.textTheme.bodySmall)
                            : null,
                        onTap: _busy
                            ? null
                            : () => setState(() {
                                  _selected = d;
                                  _confirm.clear();
                                  _error = null;
                                }),
                      );
                    },
                  ),
                ),
                if (_needsErase) ...[
                  const Divider(height: 24),
                  Text('This disk must be erased',
                      style: theme.textTheme.titleSmall
                          ?.copyWith(color: AraColors.accentBusy)),
                  const SizedBox(height: 6),
                  Text(
                    'ARA saves frames in a format this disk doesn\'t use, so it '
                    'has to be erased first. Everything on it will be lost.',
                    style: theme.textTheme.bodySmall,
                  ),
                  const SizedBox(height: 8),
                  RadioGroup<String>(
                    groupValue: _newFilesystem,
                    onChanged: (v) =>
                        setState(() => _newFilesystem = v ?? 'exfat'),
                    child: Column(
                      children: [
                        RadioListTile<String>(
                          value: 'exfat',
                          dense: true,
                          contentPadding: EdgeInsets.zero,
                          title: const Text('exFAT — take the drive with you'),
                          subtitle: Text(
                              'Plugs straight into Windows and Mac at home. '
                              'After a power cut, run Check disk.',
                              style: theme.textTheme.bodySmall),
                        ),
                        RadioListTile<String>(
                          value: 'ext4',
                          dense: true,
                          contentPadding: EdgeInsets.zero,
                          title: const Text('ext4 — drive lives on the rig'),
                          subtitle: Text(
                              'Journaled (self-healing after power cuts); '
                              'computers can\'t read it directly — frames '
                              'arrive via the backup mirror.',
                              style: theme.textTheme.bodySmall),
                        ),
                      ],
                    ),
                  ),
                  const SizedBox(height: 10),
                  Text(
                      (_selected!.label ?? '').isNotEmpty
                          ? 'Type “${_selected!.label}” to confirm:'
                          : 'This disk has no label — type ERASE to confirm:',
                      style: theme.textTheme.bodySmall),
                  const SizedBox(height: 4),
                  TextField(
                    controller: _confirm,
                    autofocus: true,
                    enabled: !_busy,
                    onChanged: (_) => setState(() {}),
                    decoration: const InputDecoration(isDense: true),
                  ),
                ],
                if (_error != null) ...[
                  const SizedBox(height: 12),
                  Text(_error!,
                      style: theme.textTheme.bodySmall
                          ?.copyWith(color: AraColors.accentBusy)),
                ],
                if (_busy) ...[
                  const SizedBox(height: 12),
                  Row(children: [
                    const SizedBox(
                        width: 14,
                        height: 14,
                        child: CircularProgressIndicator(strokeWidth: 2)),
                    const SizedBox(width: 10),
                    Text(
                        _needsErase
                            ? 'Erasing and connecting the disk…'
                            : 'Connecting the disk…',
                        style: theme.textTheme.bodySmall),
                  ]),
                ],
              ],
            );
          },
        ),
      ),
      actions: [
        TextButton(
          onPressed: _busy ? null : () => ref.invalidate(storageDevicesProvider),
          child: const Text('Rescan'),
        ),
        TextButton(
          onPressed: _busy ? null : () => Navigator.of(context).pop(),
          child: const Text('Cancel'),
        ),
        FilledButton(
          style: _needsErase
              ? FilledButton.styleFrom(backgroundColor: AraColors.accentBusy)
              : null,
          onPressed: _busy ||
                  _selected == null ||
                  !_selected!.selectable ||
                  !_confirmSatisfied
              ? null
              : _useDisk,
          child: Text(_needsErase ? 'Erase and Use Disk' : 'Use Disk'),
        ),
      ],
    );
  }
}
