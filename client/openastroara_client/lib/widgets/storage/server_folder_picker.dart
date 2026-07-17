import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../models/server.dart';
import '../../services/storage_browse_api.dart';
import '../../theme/ara_colors.dart';

/// Injectable factory so widget tests can hand the dialog a fake walker.
final storageBrowseApiFactoryProvider =
    Provider<StorageBrowseApi Function(AraServer)>((_) => StorageBrowseApi.new);

/// §37.4/§29 — modal picker that walks the SERVER's filesystem (the daemon on
/// the Pi/SBC) so the user can point frame saving at an internal folder or a
/// USB drive without typing paths blind. Returns the picked directory path, or
/// null when dismissed.
Future<String?> showServerFolderPicker(
  BuildContext context,
  WidgetRef ref, {
  required AraServer server,
  String? startPath,
}) {
  final api = ref.read(storageBrowseApiFactoryProvider)(server);
  return showDialog<String?>(
    context: context,
    builder: (_) => _ServerFolderPickerDialog(api: api, startPath: startPath),
  ).whenComplete(api.close);
}

/// Name prompt for "New folder" — owns its TextEditingController so it is
/// disposed with the dialog's own lifecycle (disposing it right after
/// showDialog returns races the pop animation).
class _NewFolderNameDialog extends StatefulWidget {
  const _NewFolderNameDialog();

  @override
  State<_NewFolderNameDialog> createState() => _NewFolderNameDialogState();
}

class _NewFolderNameDialogState extends State<_NewFolderNameDialog> {
  final _controller = TextEditingController();

  @override
  void dispose() {
    _controller.dispose();
    super.dispose();
  }

  @override
  Widget build(BuildContext context) {
    return AlertDialog(
      title: const Text('New folder'),
      content: TextField(
        controller: _controller,
        autofocus: true,
        decoration: const InputDecoration(
          labelText: 'Folder name',
          hintText: 'e.g. Lights',
        ),
        onSubmitted: (v) => Navigator.of(context).pop(v),
      ),
      actions: [
        TextButton(
            onPressed: () => Navigator.of(context).pop(null),
            child: const Text('Cancel')),
        FilledButton(
            onPressed: () => Navigator.of(context).pop(_controller.text),
            child: const Text('Create')),
      ],
    );
  }
}

class _ServerFolderPickerDialog extends StatefulWidget {
  const _ServerFolderPickerDialog({required this.api, this.startPath});
  final StorageBrowseApi api;
  final String? startPath;

  @override
  State<_ServerFolderPickerDialog> createState() =>
      _ServerFolderPickerDialogState();
}

class _ServerFolderPickerDialogState extends State<_ServerFolderPickerDialog> {
  StorageBrowseLevel? _level;
  String? _error;
  // Transient message for a failed "New folder" — shown under the listing
  // without replacing it (unlike _error, which replaces a failed listing).
  String? _notice;
  bool _loading = true;

  @override
  void initState() {
    super.initState();
    // Start at the field's current directory when it exists; a 404 there (a
    // path from another rig) falls back to the curated roots instead of an
    // error wall.
    _go(widget.startPath, fallbackToRoots: true);
  }

  Future<void> _go(String? path, {bool fallbackToRoots = false}) async {
    setState(() {
      _loading = true;
      _error = null;
      _notice = null;
    });
    try {
      final level = await widget.api.browse(path);
      if (!mounted) return;
      setState(() {
        _level = level;
        _loading = false;
      });
    } catch (e) {
      if (!mounted) return;
      if (fallbackToRoots && path != null) {
        await _go(null);
        return;
      }
      setState(() {
        _loading = false;
        _error = 'Couldn\'t list that folder on the server.';
      });
    }
  }

  /// Ask for a name, create it under the current directory, and show the
  /// refreshed listing (the daemon responds with the parent re-listed).
  Future<void> _newFolder(StorageBrowseLevel level) async {
    final name = await showDialog<String?>(
      context: context,
      builder: (_) => const _NewFolderNameDialog(),
    );
    final trimmed = name?.trim();
    if (trimmed == null || trimmed.isEmpty || !mounted) return;
    try {
      final refreshed = await widget.api.createFolder(level.path, trimmed);
      if (!mounted) return;
      setState(() {
        _level = refreshed;
        _notice = null;
      });
    } catch (e) {
      if (!mounted) return;
      setState(() => _notice =
          'Couldn\'t create "$trimmed" here — check the name and permissions.');
    }
  }

  @override
  Widget build(BuildContext context) {
    final level = _level;
    return AlertDialog(
      title: Column(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          const Text('Choose a folder on the server'),
          if (level != null && !level.isRoots) ...[
            const SizedBox(height: 4),
            Text(level.path,
                style: const TextStyle(
                    fontSize: 12, color: AraColors.textSecondary)),
          ],
        ],
      ),
      content: SizedBox(
        width: 440,
        height: 360,
        child: _loading
            ? const Center(child: CircularProgressIndicator())
            : _error != null
                ? Center(
                    child: Text(_error!,
                        style:
                            const TextStyle(color: AraColors.textSecondary)))
                : ListView(
                    children: [
                      if (level != null && !level.isRoots)
                        ListTile(
                          dense: true,
                          leading: const Icon(Icons.arrow_upward, size: 18),
                          title: const Text('Up one level'),
                          // Parent null = filesystem root's parent → the
                          // curated roots listing.
                          onTap: () => _go(level.parent),
                        ),
                      for (final d in level?.dirs ?? const <StorageBrowseEntry>[])
                        ListTile(
                          dense: true,
                          leading: Icon(
                            d.removable ? Icons.usb : Icons.folder_outlined,
                            size: 18,
                            color: d.removable
                                ? AraColors.accentConnected
                                : AraColors.textSecondary,
                          ),
                          title: Text(d.name),
                          subtitle: d.removable
                              ? const Text('Removable / USB',
                                  style: TextStyle(fontSize: 11))
                              : null,
                          onTap: () => _go(d.path),
                        ),
                      if (level != null && level.dirs.isEmpty && !level.isRoots)
                        const Padding(
                          padding: EdgeInsets.all(16),
                          child: Text('No subfolders here.',
                              style:
                                  TextStyle(color: AraColors.textSecondary)),
                        ),
                    ],
                  ),
      ),
      actions: [
        if (_notice != null)
          Padding(
            padding: const EdgeInsets.only(right: 8),
            child: Text(_notice!,
                style: const TextStyle(
                    fontSize: 12, color: AraColors.textSecondary)),
          ),
        Tooltip(
          message: level != null && !level.isRoots && level.writable
              ? 'Create a subfolder here'
              : 'Open a writable folder first',
          child: TextButton.icon(
            onPressed: level != null && !level.isRoots && level.writable
                ? () => _newFolder(level)
                : null,
            icon: const Icon(Icons.create_new_folder_outlined, size: 18),
            label: const Text('New folder'),
          ),
        ),
        TextButton(
          onPressed: () => Navigator.of(context).pop(null),
          child: const Text('Cancel'),
        ),
        Tooltip(
          message: level != null && !level.isRoots && !level.writable
              ? 'The daemon can\'t write here — pick another folder.'
              : 'Save frames into this folder',
          child: FilledButton(
            onPressed: level != null && !level.isRoots && level.writable
                ? () => Navigator.of(context).pop(level.path)
                : null,
            child: const Text('Use this folder'),
          ),
        ),
      ],
    );
  }
}
