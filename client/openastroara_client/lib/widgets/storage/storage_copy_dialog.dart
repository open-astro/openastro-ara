import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../services/storage_browse_api.dart';
import '../../services/storage_copy_api.dart';
import '../../services/storage_devices_api.dart';
import '../../state/saved_server_state.dart';
import '../../theme/ara_colors.dart';
import '../../util/friendly_error.dart';

/// Copy the store — all of it, or folders picked on it — onto a second drive
/// plugged into the server. Copy only: nothing on either drive is deleted or
/// overwritten, and re-running a copy only adds what is new.
Future<void> showStorageCopyDialog(BuildContext context) async {
  await showDialog<void>(
    context: context,
    builder: (_) => const _CopyDialog(),
  );
}

/// Drives a copy can go to: plugged in, not the store, not the server's own
/// disk, and in a format ARA writes (exFAT or ext4).
List<StorageDevice> copyTargets(List<StorageDevice> devices) => devices
    .where((d) =>
        !d.isSystemDisk && !d.isAraStore && d.isMountable && d.uuid != null)
    .toList();

String _size(int bytes) {
  const units = ['B', 'KB', 'MB', 'GB', 'TB'];
  var value = bytes.toDouble();
  var unit = 0;
  while (value >= 1000 && unit < units.length - 1) {
    value /= 1000;
    unit++;
  }
  return unit == 0
      ? '$bytes B'
      : '${value.toStringAsFixed(value >= 100 ? 0 : 1)} ${units[unit]}';
}

String _files(int n) => n == 1 ? '1 file' : '$n files';

/// Why a copy did not happen (start refused, or the job failed), in the
/// user's terms.
String copyFailureMessage(String code, String? detail, {int bytesToCopy = 0}) =>
    switch (code) {
      'not_enough_space' => bytesToCopy > 0
          ? 'The drive doesn\'t have room for ${_size(bytesToCopy)}.'
          : 'The drive doesn\'t have enough room.',
      'refused' when detail == 'is_store' =>
        'That drive is the one frames are saved to.',
      'refused' || 'system_disk' =>
        'That drive holds the server\'s operating system.',
      'device_busy' when detail == 'mounted_elsewhere' =>
        'That drive is in use somewhere else on the server.',
      'device_busy' => 'The drive is busy. Try again in a moment.',
      'not_ext4' =>
        'ARA can only write to exFAT or ext4 drives, and this one is neither.',
      'mount_failed' => 'The drive could not be connected.',
      'uuid_not_found' => 'The drive disconnected. Plug it back in and try again.',
      'no_store' => 'No storage drive is connected, so there is nothing to copy.',
      'copy_running' => 'A copy is already running.',
      'bad_folder' => 'One of the folders is no longer on the drive.',
      'helper_missing' =>
        'The server is missing part of its installation. Reinstall openastroara-server.',
      _ => detail ?? 'The copy failed ($code).',
    };

/// The outcome of a finished copy, one sentence per thing worth knowing.
List<String> copySummary(StorageCopyStatus s) {
  final lines = <String>[];
  switch (s.state) {
    case 'done':
      lines.add(s.filesCopied == 0
          ? 'Nothing new to copy.'
          : 'Copied ${_files(s.filesCopied)} (${_size(s.bytesCopied)}).');
    case 'cancelled':
      lines.add('Copy cancelled after ${_files(s.filesCopied)}. '
          'Files already copied stay on the drive.');
    case 'failed':
      lines.add(copyFailureMessage(s.errorCode ?? 'copy_failed', s.errorDetail,
          bytesToCopy: s.bytesToCopy));
  }
  if (s.filesSkipped > 0) {
    lines.add('${_files(s.filesSkipped)} were already on the drive.');
  }
  if (s.filesConflicting > 0) {
    lines.add('${_files(s.filesConflicting)} had a different file with the '
        'same name on the drive and were left alone.');
  }
  if (s.filesFailed > 0) {
    lines.add('${_files(s.filesFailed)} could not be copied'
        '${s.firstFailure == null ? '.' : ' (first: ${s.firstFailure}).'}');
  }
  if (s.targetReleased) {
    lines.add('Safe to unplug the drive.');
  } else if (s.errorCode == 'release_failed') {
    lines.add('The drive is still connected. Eject it before unplugging.');
  }
  return lines;
}

class _CopyDialog extends ConsumerStatefulWidget {
  const _CopyDialog();

  @override
  ConsumerState<_CopyDialog> createState() => _CopyDialogState();
}

class _CopyDialogState extends ConsumerState<_CopyDialog> {
  String? _targetUuid;
  bool _pickFolders = false;
  final Set<String> _picked = {};
  bool _busy = false;
  String? _error;

  /// Set once this dialog started a copy, so its result stays on screen.
  bool _started = false;

  Future<void> _start() async {
    final server = ref.read(activeServerProvider);
    final target = _targetUuid;
    if (server == null || target == null) return;
    setState(() {
      _busy = true;
      _error = null;
    });
    final api = StorageCopyApi(server);
    try {
      await api.start(
        targetUuid: target,
        folders: _pickFolders ? _picked.toList() : const [],
      );
      if (!mounted) return;
      _started = true;
      ref.invalidate(storageCopyStatusProvider);
    } on StorageCopyRefused catch (e) {
      if (mounted) setState(() => _error = copyFailureMessage(e.code, e.detail));
    } catch (e) {
      if (mounted) {
        setState(() => _error = friendlyError(e, action: 'start the copy'));
      }
    } finally {
      api.close();
      if (mounted) setState(() => _busy = false);
    }
  }

  Future<void> _cancel() async {
    final server = ref.read(activeServerProvider);
    if (server == null) return;
    final api = StorageCopyApi(server);
    try {
      await api.cancel();
    } catch (e) {
      if (mounted) {
        setState(() => _error = friendlyError(e, action: 'cancel the copy'));
      }
    } finally {
      api.close();
    }
  }

  @override
  Widget build(BuildContext context) {
    final status = ref.watch(storageCopyStatusProvider).asData?.value;
    final showJob =
        status != null && (status.isRunning || (_started && status.isFinished));
    return AlertDialog(
      title: const Text('Copy to another disk'),
      content: SizedBox(
        width: 520,
        child: showJob ? _progress(status) : _setup(),
      ),
      actions: showJob
          ? [
              if (status.isRunning)
                TextButton(onPressed: _cancel, child: const Text('Cancel copy')),
              FilledButton(
                onPressed: () => Navigator.of(context).pop(),
                child: Text(status.isRunning ? 'Hide' : 'Done'),
              ),
            ]
          : [
              TextButton(
                onPressed:
                    _busy ? null : () => ref.invalidate(storageDevicesProvider),
                child: const Text('Rescan'),
              ),
              TextButton(
                onPressed: _busy ? null : () => Navigator.of(context).pop(),
                child: const Text('Cancel'),
              ),
              FilledButton(
                onPressed: _busy ||
                        _targetUuid == null ||
                        (_pickFolders && _picked.isEmpty)
                    ? null
                    : _start,
                child: const Text('Copy'),
              ),
            ],
    );
  }

  Widget _setup() {
    final theme = Theme.of(context);
    final devices = ref.watch(storageDevicesProvider);
    return devices.when(
      loading: () => const SizedBox(
          height: 140, child: Center(child: CircularProgressIndicator())),
      error: (e, _) => SizedBox(
        height: 140,
        child: Center(
            child: Text(friendlyError(e, action: "read the server's disks"),
                textAlign: TextAlign.center)),
      ),
      data: (all) {
        final store = all.where((d) => d.isAraStore).firstOrNull;
        final targets = copyTargets(all);
        if (_targetUuid != null && !targets.any((d) => d.uuid == _targetUuid)) {
          _targetUuid = null; // unplugged since it was picked
        }
        if (store == null) {
          return const SizedBox(
            height: 120,
            child: Center(
              child: Text('Choose a storage drive first: copies are made from it.',
                  textAlign: TextAlign.center),
            ),
          );
        }
        if (targets.isEmpty) {
          return const SizedBox(
            height: 140,
            child: Center(
              child: Text(
                'No other drive to copy to.\n\n'
                'Plug a second USB drive (exFAT or ext4) into the server, '
                'then Rescan.',
                textAlign: TextAlign.center,
              ),
            ),
          );
        }
        return Column(
          mainAxisSize: MainAxisSize.min,
          crossAxisAlignment: CrossAxisAlignment.start,
          children: [
            // Drives formatted by ARA all carry the label ARA-DISK: the size and
            // model are what tell the source from the target.
            Text(
                store.detailLine.isEmpty
                    ? 'From ${store.friendlyName} to:'
                    : 'From ${store.friendlyName} (${store.detailLine}) to:',
                style: theme.textTheme.bodySmall),
            const SizedBox(height: 4),
            for (final d in targets)
              ListTile(
                dense: true,
                contentPadding: EdgeInsets.zero,
                selected: d.uuid == _targetUuid,
                selectedTileColor:
                    AraColors.textSecondary.withValues(alpha: 0.12),
                leading: Icon(d.removable ? Icons.usb : Icons.storage),
                title: Text(d.friendlyName),
                subtitle: Text(d.detailLine),
                onTap: _busy ? null : () => setState(() => _targetUuid = d.uuid),
              ),
            const Divider(height: 20),
            RadioGroup<bool>(
              groupValue: _pickFolders,
              onChanged: (v) {
                if (!_busy) setState(() => _pickFolders = v ?? false);
              },
              child: const Column(
                children: [
                  RadioListTile<bool>(
                    value: false,
                    dense: true,
                    contentPadding: EdgeInsets.zero,
                    title: Text('Everything on the drive'),
                  ),
                  RadioListTile<bool>(
                    value: true,
                    dense: true,
                    contentPadding: EdgeInsets.zero,
                    title: Text('Choose folders'),
                  ),
                ],
              ),
            ),
            if (_pickFolders && store.mountPoint != null)
              _FolderChecklist(
                root: store.mountPoint!,
                picked: _picked,
                onChanged: () => setState(() {}),
              ),
            const SizedBox(height: 8),
            Text(
              'Files already on the drive are skipped, and nothing is deleted '
              'or overwritten on either drive.',
              style: theme.textTheme.bodySmall
                  ?.copyWith(color: AraColors.textSecondary),
            ),
            if (_error != null) ...[
              const SizedBox(height: 12),
              Text(_error!,
                  style: theme.textTheme.bodySmall
                      ?.copyWith(color: AraColors.accentBusy)),
            ],
          ],
        );
      },
    );
  }

  Widget _progress(StorageCopyStatus s) {
    final theme = Theme.of(context);
    if (s.isFinished) {
      return Column(
        mainAxisSize: MainAxisSize.min,
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          for (final line in copySummary(s))
            Padding(
              padding: const EdgeInsets.only(bottom: 6),
              child: Text(line),
            ),
        ],
      );
    }
    return Column(
      mainAxisSize: MainAxisSize.min,
      crossAxisAlignment: CrossAxisAlignment.start,
      children: [
        Text(
          s.state == 'preparing'
              ? 'Connecting the drive and listing files…'
              : 'Copying ${s.filesCopied + s.filesFailed} of '
                  '${_files(s.filesToCopy)} '
                  '(${_size(s.bytesCopied)} of ${_size(s.bytesToCopy)})',
        ),
        const SizedBox(height: 10),
        ClipRRect(
          borderRadius: BorderRadius.circular(4),
          child: LinearProgressIndicator(
            value: s.fraction,
            minHeight: 8,
            // Same muted track as the drive card's usage bar: the default track
            // is brighter than the fill in the dark theme and reads inverted.
            backgroundColor: AraColors.textSecondary.withValues(alpha: 0.2),
          ),
        ),
        if (s.currentFile != null) ...[
          const SizedBox(height: 8),
          Text(s.currentFile!,
              maxLines: 1,
              overflow: TextOverflow.ellipsis,
              style: theme.textTheme.bodySmall
                  ?.copyWith(color: AraColors.textSecondary)),
        ],
        const SizedBox(height: 10),
        Text(
          'You can hide this window; the copy keeps going on the server.',
          style: theme.textTheme.bodySmall
              ?.copyWith(color: AraColors.textSecondary),
        ),
        if (_error != null) ...[
          const SizedBox(height: 12),
          Text(_error!,
              style: theme.textTheme.bodySmall
                  ?.copyWith(color: AraColors.accentBusy)),
        ],
      ],
    );
  }
}

/// One folder level of the store with a checkbox per folder; tapping a name
/// opens it. A ticked folder includes everything under it, so its children
/// show ticked and locked.
class _FolderChecklist extends ConsumerStatefulWidget {
  const _FolderChecklist({
    required this.root,
    required this.picked,
    required this.onChanged,
  });

  final String root;
  final Set<String> picked;
  final VoidCallback onChanged;

  @override
  ConsumerState<_FolderChecklist> createState() => _FolderChecklistState();
}

class _FolderChecklistState extends ConsumerState<_FolderChecklist> {
  late String _path = widget.root;
  StorageBrowseLevel? _level;
  String? _error;

  @override
  void initState() {
    super.initState();
    _load(widget.root);
  }

  Future<void> _load(String path) async {
    final server = ref.read(activeServerProvider);
    if (server == null) return;
    final api = StorageBrowseApi(server);
    try {
      final level = await api.browse(path);
      if (!mounted) return;
      setState(() {
        _path = level.path;
        _level = level;
        _error = null;
      });
    } catch (e) {
      if (mounted) {
        setState(() => _error = friendlyError(e, action: 'list the folders'));
      }
    } finally {
      api.close();
    }
  }

  bool _underPicked(String path) =>
      widget.picked.any((p) => path.startsWith('$p/'));

  String _relative(String path) =>
      path == widget.root ? '' : path.substring(widget.root.length + 1);

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    final level = _level;
    return Container(
      constraints: const BoxConstraints(maxHeight: 240),
      decoration: BoxDecoration(
        border: Border.all(color: AraColors.textSecondary.withValues(alpha: 0.35)),
        borderRadius: BorderRadius.circular(6),
      ),
      child: Column(
        mainAxisSize: MainAxisSize.min,
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          Row(
            children: [
              IconButton(
                tooltip: 'Up',
                icon: const Icon(Icons.arrow_upward, size: 18),
                onPressed: _path == widget.root || level?.parent == null
                    ? null
                    : () => _load(level!.parent!),
              ),
              Expanded(
                child: Text(
                  _path == widget.root ? 'Storage drive' : _relative(_path),
                  overflow: TextOverflow.ellipsis,
                  style: theme.textTheme.bodySmall,
                ),
              ),
            ],
          ),
          if (_error != null)
            Padding(
              padding: const EdgeInsets.all(8),
              child: Text(_error!, style: theme.textTheme.bodySmall),
            )
          else if (level == null)
            const Padding(
              padding: EdgeInsets.all(12),
              child: Center(child: CircularProgressIndicator(strokeWidth: 2)),
            )
          else if (level.dirs.isEmpty)
            Padding(
              padding: const EdgeInsets.all(8),
              child: Text('No folders here.', style: theme.textTheme.bodySmall),
            )
          else
            Flexible(
              child: ListView(
                shrinkWrap: true,
                children: [
                  for (final dir in level.dirs)
                    CheckboxListTile(
                      dense: true,
                      controlAffinity: ListTileControlAffinity.leading,
                      value: widget.picked.contains(dir.path) ||
                          _underPicked(dir.path),
                      onChanged: _underPicked(dir.path)
                          ? null
                          : (v) {
                              if (v ?? false) {
                                widget.picked
                                    .removeWhere((p) => p.startsWith('${dir.path}/'));
                                widget.picked.add(dir.path);
                              } else {
                                widget.picked.remove(dir.path);
                              }
                              widget.onChanged();
                              setState(() {});
                            },
                      title: Text(dir.name),
                      secondary: IconButton(
                        tooltip: 'Open',
                        icon: const Icon(Icons.chevron_right, size: 18),
                        onPressed: () => _load(dir.path),
                      ),
                    ),
                ],
              ),
            ),
        ],
      ),
    );
  }
}
