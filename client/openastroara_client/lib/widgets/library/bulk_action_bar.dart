import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../state/library/library_selection.dart';
import '../../state/library/live_library_state.dart';
import '../../theme/ara_colors.dart';

/// §40.8 bulk action bar — slides up from the bottom when selection is
/// non-empty. 12f.3b: Rate / Tag / Delete are live against
/// `/api/v1/frames/bulk/*`; Move-to-session and Export stay disabled (no
/// server endpoints yet — tracked in PORT_TODO).
class LibraryBulkActionBar extends ConsumerWidget {
  const LibraryBulkActionBar({super.key});

  /// Run a bulk call, then clear the selection and refresh the strips so the
  /// updated ratings/tags/deletions are visible immediately.
  static Future<void> _apply(
    BuildContext context,
    WidgetRef ref,
    Future<void> Function() call,
    String successMessage,
  ) async {
    try {
      await call();
      if (!context.mounted) return;
      ref.read(librarySelectionProvider.notifier).clear();
      ref.invalidate(sessionFramesProvider);
      ref.invalidate(liveLibrarySessionsProvider);
      ScaffoldMessenger.of(context)
          .showSnackBar(SnackBar(content: Text(successMessage)));
    } on Exception catch (e) {
      if (!context.mounted) return;
      ScaffoldMessenger.of(context)
          .showSnackBar(SnackBar(content: Text('Bulk operation failed: $e')));
    }
  }

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final selected = ref.watch(librarySelectionProvider);
    if (selected.isEmpty) return const SizedBox.shrink();
    final ids = selected.toList(growable: false);
    final api = ref.watch(libraryApiProvider);

    return SafeArea(
      // `top: false` because this bar lives at the bottom; only the bottom
      // inset matters (system gesture/navigation area on mobile, Dock on
      // macOS, etc).
      top: false,
      child: Material(
        elevation: 8,
        color: AraColors.bgPanel,
        child: Container(
          decoration: const BoxDecoration(
            border: Border(top: BorderSide(color: AraColors.selectionBg, width: 2)),
          ),
          padding: const EdgeInsets.symmetric(horizontal: 12, vertical: 8),
          child: Row(
            children: [
              IconButton(
                onPressed: () =>
                    ref.read(librarySelectionProvider.notifier).clear(),
                icon: const Icon(Icons.close),
                tooltip: 'Exit selection mode',
              ),
              Text(
                '${selected.length} selected',
                style: Theme.of(context).textTheme.titleSmall,
              ),
              const SizedBox(width: 16),
              Expanded(
                child: SingleChildScrollView(
                  scrollDirection: Axis.horizontal,
                  child: Row(children: [
                    _BulkAction(
                      icon: Icons.star_border,
                      label: 'Rate',
                      onPressed: api == null
                          ? null
                          : () async {
                              final rating = await showDialog<int>(
                                context: context,
                                builder: (_) => const _RatePickerDialog(),
                              );
                              if (rating == null || !context.mounted) return;
                              await _apply(
                                  context,
                                  ref,
                                  () => api.bulkRate(ids, rating),
                                  rating == 0
                                      ? 'Cleared rating on ${ids.length} frame(s).'
                                      : 'Rated ${ids.length} frame(s) $rating-star.');
                            },
                    ),
                    _BulkAction(
                      icon: Icons.label_outline,
                      label: 'Tag',
                      onPressed: api == null
                          ? null
                          : () async {
                              final tags = await showDialog<_TagEdit>(
                                context: context,
                                builder: (_) => const _TagDialog(),
                              );
                              if (tags == null || !context.mounted) return;
                              await _apply(
                                  context,
                                  ref,
                                  () => api.bulkTag(ids,
                                      addTags: tags.add,
                                      removeTags: tags.remove),
                                  'Updated tags on ${ids.length} frame(s).');
                            },
                    ),
                    _BulkAction(
                      icon: Icons.drive_file_move_outlined,
                      label: 'Move to session',
                      onPressed: api == null
                          ? null
                          : () async {
                              final target = await showDialog<String>(
                                context: context,
                                builder: (_) => const _SessionPickerDialog(),
                              );
                              if (target == null || !context.mounted) return;
                              await _apply(
                                  context,
                                  ref,
                                  () => api.bulkMove(ids, target),
                                  'Moved ${ids.length} frame(s).');
                            },
                    ),
                    // Export: no server endpoint yet (tarball per §39.10).
                    const _BulkAction(
                        icon: Icons.file_download_outlined, label: 'Export'),
                    _BulkAction(
                      icon: Icons.delete_outline,
                      label: 'Delete',
                      destructive: true,
                      onPressed: api == null
                          ? null
                          : () async {
                              final fromDisk = await showDialog<bool>(
                                context: context,
                                builder: (_) =>
                                    _DeleteConfirmDialog(count: ids.length),
                              );
                              if (fromDisk == null || !context.mounted) return;
                              await _apply(
                                  context,
                                  ref,
                                  () => api.bulkDelete(ids,
                                      deleteFromDisk: fromDisk),
                                  'Deleted ${ids.length} frame(s)'
                                  '${fromDisk ? ' and their files' : ' from the catalog'}.');
                            },
                    ),
                  ]),
                ),
              ),
            ],
          ),
        ),
      ),
    );
  }
}

class _BulkAction extends StatelessWidget {
  final IconData icon;
  final String label;
  final bool destructive;
  final VoidCallback? onPressed;
  const _BulkAction({
    required this.icon,
    required this.label,
    this.destructive = false,
    this.onPressed,
  });

  @override
  Widget build(BuildContext context) {
    return Padding(
      padding: const EdgeInsets.symmetric(horizontal: 4),
      child: TextButton.icon(
        onPressed: onPressed,
        icon: Icon(icon, size: 16),
        label: Text(label),
        style: TextButton.styleFrom(
          foregroundColor: destructive ? AraColors.accentBusy : null,
        ),
      ),
    );
  }
}

/// Pick the target session for a bulk move from the loaded session list.
/// Pops null on cancel.
class _SessionPickerDialog extends ConsumerWidget {
  const _SessionPickerDialog();

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final sessions = ref.watch(liveLibrarySessionsProvider).value ?? const [];
    return SimpleDialog(
      title: const Text('Move to session'),
      children: [
        if (sessions.isEmpty)
          const Padding(
            padding: EdgeInsets.all(16),
            child: Text('No sessions loaded.'),
          )
        else
          for (final s in sessions)
            SimpleDialogOption(
              onPressed: () => Navigator.of(context).pop(s.id),
              child: Text(
                  '${s.sessionStartUtc.toLocal().toIso8601String().substring(0, 10)} — ${s.targetName}'),
            ),
      ],
    );
  }
}

/// Pick 1–5 stars, or clear (0). Pops null on cancel.
class _RatePickerDialog extends StatelessWidget {
  const _RatePickerDialog();

  @override
  Widget build(BuildContext context) {
    return AlertDialog(
      title: const Text('Rate selected frames'),
      content: Row(
        mainAxisSize: MainAxisSize.min,
        children: [
          for (var stars = 1; stars <= 5; stars++)
            IconButton(
              tooltip: '$stars star${stars == 1 ? '' : 's'}',
              onPressed: () => Navigator.of(context).pop(stars),
              icon: Icon(Icons.star,
                  color: AraColors.accentBusy, size: 20 + stars.toDouble()),
            ),
        ],
      ),
      actions: [
        TextButton(
          onPressed: () => Navigator.of(context).pop(0),
          child: const Text('Clear rating'),
        ),
        TextButton(
          onPressed: () => Navigator.of(context).pop(),
          child: const Text('Cancel'),
        ),
      ],
    );
  }
}

class _TagEdit {
  final List<String> add;
  final List<String> remove;
  const _TagEdit(this.add, this.remove);
}

/// Comma-separated add/remove tag lists. Pops null on cancel or no-op input.
class _TagDialog extends StatefulWidget {
  const _TagDialog();

  @override
  State<_TagDialog> createState() => _TagDialogState();
}

class _TagDialogState extends State<_TagDialog> {
  final _add = TextEditingController();
  final _remove = TextEditingController();

  @override
  void dispose() {
    _add.dispose();
    _remove.dispose();
    super.dispose();
  }

  List<String> _split(String text) => text
      .split(',')
      .map((t) => t.trim())
      .where((t) => t.isNotEmpty)
      .toList(growable: false);

  @override
  Widget build(BuildContext context) {
    return AlertDialog(
      title: const Text('Tag selected frames'),
      content: Column(
        mainAxisSize: MainAxisSize.min,
        children: [
          TextField(
            controller: _add,
            decoration:
                const InputDecoration(labelText: 'Add tags (comma-separated)'),
          ),
          const SizedBox(height: 8),
          TextField(
            controller: _remove,
            decoration: const InputDecoration(
                labelText: 'Remove tags (comma-separated)'),
          ),
        ],
      ),
      actions: [
        TextButton(
          onPressed: () => Navigator.of(context).pop(),
          child: const Text('Cancel'),
        ),
        FilledButton(
          onPressed: () {
            final add = _split(_add.text);
            final remove = _split(_remove.text);
            if (add.isEmpty && remove.isEmpty) {
              Navigator.of(context).pop();
              return;
            }
            Navigator.of(context).pop(_TagEdit(add, remove));
          },
          child: const Text('Apply'),
        ),
      ],
    );
  }
}

/// Confirm deletion; returns null on cancel, else whether to also delete the
/// FITS files from disk (default: catalog-only).
class _DeleteConfirmDialog extends StatefulWidget {
  final int count;
  const _DeleteConfirmDialog({required this.count});

  @override
  State<_DeleteConfirmDialog> createState() => _DeleteConfirmDialogState();
}

class _DeleteConfirmDialogState extends State<_DeleteConfirmDialog> {
  bool _fromDisk = false;

  @override
  Widget build(BuildContext context) {
    return AlertDialog(
      title: Text('Delete ${widget.count} frame(s)?'),
      content: Column(
        mainAxisSize: MainAxisSize.min,
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          const Text('Removes the frames from the catalog.'),
          CheckboxListTile(
            value: _fromDisk,
            onChanged: (v) => setState(() => _fromDisk = v ?? false),
            dense: true,
            contentPadding: EdgeInsets.zero,
            controlAffinity: ListTileControlAffinity.leading,
            title: const Text('Also delete the FITS files from disk'),
          ),
        ],
      ),
      actions: [
        TextButton(
          onPressed: () => Navigator.of(context).pop(),
          child: const Text('Cancel'),
        ),
        FilledButton(
          style:
              FilledButton.styleFrom(backgroundColor: AraColors.accentError),
          onPressed: () => Navigator.of(context).pop(_fromDisk),
          child: const Text('Delete'),
        ),
      ],
    );
  }
}
