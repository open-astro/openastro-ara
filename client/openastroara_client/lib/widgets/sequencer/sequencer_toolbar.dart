import 'package:flutter/material.dart';

import '../../theme/ara_colors.dart';

/// §25.5.3 sequencer toolbar. Phase 12d.1 renders the button row with
/// all actions disabled (no daemon connection yet). 12d.2 wires:
///   New     → reset the controller's draft to an empty root
///   Load    → list /api/v1/sequences and pick one to load
///   Save    → PUT /api/v1/sequences/{id} or POST for new
///   Validate → POST validation-only path
///   Run / Pause / Abort → POST /api/v1/sequences/{id}/start, etc.
class SequencerToolbar extends StatelessWidget {
  const SequencerToolbar({super.key});

  @override
  Widget build(BuildContext context) {
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
              child: Row(children: const [
                _ToolButton(
                    icon: Icons.note_add_outlined, label: 'New', onPressed: null),
                _ToolButton(
                    icon: Icons.folder_open_outlined,
                    label: 'Load',
                    onPressed: null),
                _ToolButton(
                    icon: Icons.save_outlined, label: 'Save', onPressed: null),
                _ToolButton(
                    icon: Icons.fact_check_outlined,
                    label: 'Validate',
                    onPressed: null),
                VerticalDivider(width: 16, indent: 8, endIndent: 8),
                _ToolButton(
                    icon: Icons.play_arrow, label: 'Run', onPressed: null),
                _ToolButton(icon: Icons.pause, label: 'Pause', onPressed: null),
                _ToolButton(icon: Icons.stop, label: 'Abort', onPressed: null),
              ]),
            ),
          ),
          Expanded(
            child: Text(
              'Idle — connect to a server to load saved sequences',
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
