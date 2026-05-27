import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../theme/ara_colors.dart';
import '../status_indicator.dart';

/// Always-visible diagnostic state per playbook §51. Renders inline below
/// the histogram strip. Collapsed by default; expanding shows the recent
/// diagnostic events list. Stubbed in 12c.2: a single "Diagnostics not yet
/// connected" entry sources from `diagnosticsStateProvider`. Real WS event
/// stream (`/api/v1/ws` → `diagnostics.*` events) lands in Phase 12c.3.
class DiagnosticPanel extends ConsumerStatefulWidget {
  const DiagnosticPanel({super.key});

  @override
  ConsumerState<DiagnosticPanel> createState() => _DiagnosticPanelState();
}

class _DiagnosticPanelState extends ConsumerState<DiagnosticPanel> {
  bool _expanded = false;

  @override
  Widget build(BuildContext context) {
    final diag = ref.watch(diagnosticsStateProvider);
    return Container(
      decoration: const BoxDecoration(
        color: AraColors.bgPanel,
        border: Border(top: BorderSide(color: AraColors.border)),
      ),
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.stretch,
        children: [
          InkWell(
            onTap: () => setState(() => _expanded = !_expanded),
            child: Padding(
              padding: const EdgeInsets.symmetric(horizontal: 12, vertical: 6),
              child: Row(
                children: [
                  Icon(_expanded ? Icons.expand_more : Icons.chevron_right,
                      size: 18, color: AraColors.textSecondary),
                  const SizedBox(width: 4),
                  StatusIndicator(level: diag.level, label: diag.label),
                  const Spacer(),
                  Text(
                    diag.events.isEmpty
                        ? 'No recent events'
                        : '${diag.events.length} event${diag.events.length == 1 ? '' : 's'}',
                    style: Theme.of(context).textTheme.bodySmall?.copyWith(
                          color: AraColors.textSecondary,
                        ),
                  ),
                ],
              ),
            ),
          ),
          if (_expanded)
            ConstrainedBox(
              constraints: const BoxConstraints(maxHeight: 160),
              child: ListView.builder(
                shrinkWrap: true,
                itemCount: diag.events.length,
                itemBuilder: (context, i) {
                  final ev = diag.events[i];
                  return ListTile(
                    dense: true,
                    leading: Container(
                      width: 8,
                      height: 8,
                      decoration: BoxDecoration(
                        color: ev.level.color,
                        shape: BoxShape.circle,
                      ),
                    ),
                    title: Text(ev.message,
                        style: Theme.of(context).textTheme.bodySmall),
                    subtitle: Text(
                      ev.source,
                      style: Theme.of(context).textTheme.labelSmall?.copyWith(
                            color: AraColors.textDisabled,
                          ),
                    ),
                    trailing: Text(
                      _formatTime(ev.timestamp),
                      style: Theme.of(context).textTheme.labelSmall?.copyWith(
                            color: AraColors.textDisabled,
                          ),
                    ),
                  );
                },
              ),
            ),
        ],
      ),
    );
  }

  String _formatTime(DateTime t) =>
      '${t.hour.toString().padLeft(2, '0')}:${t.minute.toString().padLeft(2, '0')}:${t.second.toString().padLeft(2, '0')}';
}

/// Snapshot of the daemon's diagnostic state, mirrored from §60.9
/// `diagnostics.*` WS events. 12c.2 stubs this as "not connected"; 12c.3
/// will wire the actual event stream + history buffer.
class DiagnosticsSnapshot {
  final StatusLevel level;
  final String label;
  final List<DiagnosticEvent> events;
  const DiagnosticsSnapshot({
    required this.level,
    required this.label,
    this.events = const <DiagnosticEvent>[],
  });
}

class DiagnosticEvent {
  final DateTime timestamp;
  final StatusLevel level;
  final String source;
  final String message;
  const DiagnosticEvent({
    required this.timestamp,
    required this.level,
    required this.source,
    required this.message,
  });
}

/// Provider for the diagnostics snapshot — stub in 12c.2, real in 12c.3.
final diagnosticsStateProvider = Provider<DiagnosticsSnapshot>((ref) {
  return const DiagnosticsSnapshot(
    level: StatusLevel.disconnected,
    label: 'Diagnostics: not connected',
  );
});
