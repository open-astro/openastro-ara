import 'package:flutter/material.dart';

import '../theme/ara_colors.dart';

/// Health pill per playbook §51 + §53 a11y — always visible somewhere in the
/// app shell, summarizes overall daemon state with a single colored dot +
/// label. Tap opens the diagnostic panel (Phase 12 sub-PR follow-up).
class StatusIndicator extends StatelessWidget {
  final StatusLevel level;
  final String label;
  final VoidCallback? onTap;

  const StatusIndicator({
    super.key,
    required this.level,
    required this.label,
    this.onTap,
  });

  @override
  Widget build(BuildContext context) {
    return Semantics(
      label: 'Status: $label (${level.name})',
      button: onTap != null,
      child: InkWell(
        onTap: onTap,
        borderRadius: BorderRadius.circular(12),
        child: Padding(
          padding: const EdgeInsets.symmetric(horizontal: 10, vertical: 6),
          child: Row(
            mainAxisSize: MainAxisSize.min,
            children: [
              Container(
                width: 10,
                height: 10,
                decoration: BoxDecoration(
                  color: level.color,
                  shape: BoxShape.circle,
                ),
              ),
              const SizedBox(width: 8),
              Text(label, style: Theme.of(context).textTheme.bodySmall),
            ],
          ),
        ),
      ),
    );
  }
}

enum StatusLevel {
  connected(AraColors.accentConnected),
  busy(AraColors.accentBusy),
  error(AraColors.accentError),
  info(AraColors.accentInfo),
  disconnected(AraColors.accentDisconnected);

  final Color color;
  const StatusLevel(this.color);
}
