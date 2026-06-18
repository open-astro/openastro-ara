import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../state/sequencer/sequence_list_state.dart';
import '../../theme/ara_colors.dart';
import 'sequence_load_dialog.dart';

/// §25.5.3 sequencer toolbar. Load is wired to the §38 sequence list (opens the
/// picker once a server is connected). Save / Validate / Run / Pause / Abort
/// remain disabled pending later slices (body→tree load, then lifecycle).
class SequencerToolbar extends ConsumerWidget {
  const SequencerToolbar({super.key});

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final connected = ref.watch(sequenceApiProvider) != null;
    final selectedId = ref.watch(selectedSequenceIdProvider);

    return Container(
      height: 44,
      padding: const EdgeInsets.symmetric(horizontal: 8),
      decoration: const BoxDecoration(
        color: AraColors.bgPanel,
        border: Border(bottom: BorderSide(color: AraColors.border)),
      ),
      child: Row(
        children: [
          // Buttons in a horizontally-scrollable row so the toolbar stays
          // usable on narrow window widths (≤ ~700px). The status line
          // gets the remaining flexible space on the right.
          Flexible(
            child: SingleChildScrollView(
              scrollDirection: Axis.horizontal,
              child: Row(children: [
                const _ToolButton(
                    icon: Icons.note_add_outlined, label: 'New', onPressed: null),
                _ToolButton(
                  icon: Icons.folder_open_outlined,
                  label: 'Load',
                  // Enabled once a server is connected; opens the picker.
                  onPressed:
                      connected ? () => SequenceLoadDialog.show(context) : null,
                ),
                const _ToolButton(
                    icon: Icons.save_outlined, label: 'Save', onPressed: null),
                const _ToolButton(
                    icon: Icons.fact_check_outlined,
                    label: 'Validate',
                    onPressed: null),
                const VerticalDivider(width: 16, indent: 8, endIndent: 8),
                const _ToolButton(
                    icon: Icons.play_arrow, label: 'Run', onPressed: null),
                const _ToolButton(
                    icon: Icons.pause, label: 'Pause', onPressed: null),
                const _ToolButton(
                    icon: Icons.stop, label: 'Abort', onPressed: null),
              ]),
            ),
          ),
          Expanded(
            child: Text(
              !connected
                  ? 'Idle — connect to a server to load saved sequences'
                  : selectedId != null
                      ? 'Loaded sequence selected'
                      : 'Idle — Load a saved sequence',
              style: Theme.of(context).textTheme.bodySmall?.copyWith(
                    color: AraColors.textDisabled,
                  ),
              overflow: TextOverflow.ellipsis,
              maxLines: 1,
              textAlign: TextAlign.right,
            ),
          ),
        ],
      ),
    );
  }
}

class _ToolButton extends StatelessWidget {
  final IconData icon;
  final String label;
  final VoidCallback? onPressed;
  const _ToolButton({
    required this.icon,
    required this.label,
    required this.onPressed,
  });

  @override
  Widget build(BuildContext context) {
    return TextButton.icon(
      onPressed: onPressed,
      icon: Icon(icon, size: 16),
      label: Text(label),
      style: TextButton.styleFrom(
        foregroundColor: AraColors.textPrimary,
        disabledForegroundColor: AraColors.textDisabled,
      ),
    );
  }
}
