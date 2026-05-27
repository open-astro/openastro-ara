import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../state/library/library_selection.dart';
import '../../theme/ara_colors.dart';

/// §40.8 bulk action bar — slides up from the bottom when selection is
/// non-empty. Phase 12f.3a renders the actions but they're disabled until
/// 12f.3b wires `/api/v1/frames/bulk` (Rate / Tag / Move / Delete / Export).
class LibraryBulkActionBar extends ConsumerWidget {
  const LibraryBulkActionBar({super.key});

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final selected = ref.watch(librarySelectionProvider);
    if (selected.isEmpty) return const SizedBox.shrink();

    return Material(
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
                child: Row(children: const [
                  // All disabled until 12f.3b wires the backend bulk endpoint.
                  _BulkAction(icon: Icons.star_border, label: 'Rate'),
                  _BulkAction(icon: Icons.label_outline, label: 'Tag'),
                  _BulkAction(icon: Icons.drive_file_move_outlined, label: 'Move to session'),
                  _BulkAction(icon: Icons.file_download_outlined, label: 'Export'),
                  _BulkAction(
                    icon: Icons.delete_outline,
                    label: 'Delete',
                    destructive: true,
                  ),
                ]),
              ),
            ),
          ],
        ),
      ),
    );
  }
}

class _BulkAction extends StatelessWidget {
  final IconData icon;
  final String label;
  final bool destructive;
  const _BulkAction({
    required this.icon,
    required this.label,
    this.destructive = false,
  });

  @override
  Widget build(BuildContext context) {
    return Padding(
      padding: const EdgeInsets.symmetric(horizontal: 4),
      child: TextButton.icon(
        // Disabled until 12f.3b wires /api/v1/frames/bulk.
        onPressed: null,
        icon: Icon(icon, size: 16),
        label: Text(label),
        style: TextButton.styleFrom(
          foregroundColor: destructive ? AraColors.accentBusy : null,
        ),
      ),
    );
  }
}
