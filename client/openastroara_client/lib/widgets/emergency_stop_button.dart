import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../services/server_api.dart';
import '../theme/ara_colors.dart';
import '../state/saved_server_state.dart';
import '../util/friendly_error.dart';
import '../state/ws/client_session_state.dart';

/// §35.3 — the persistent big red button, always visible in the bottom
/// status bar. Tap → confirm ("Stop everything now?") → the daemon aborts
/// the running sequence, aborts the in-flight exposure, stops guiding,
/// parks the mount, and switches the flat panel light off. The snackbar
/// reports honestly what each rung did (a dead mount says so instead of
/// pretending it parked).
///
/// Heavier than any other control (ALP-4812): a ring of Fault Red set off by
/// a dark gap. [simple] is Simple Mode's version — 56 px tall, 10 px corners,
/// 14 px text — matching the design preview.
class EmergencyStopButton extends ConsumerStatefulWidget {
  const EmergencyStopButton({super.key, this.simple = false});

  final bool simple;

  @override
  ConsumerState<EmergencyStopButton> createState() =>
      _EmergencyStopButtonState();
}

class _EmergencyStopButtonState extends ConsumerState<EmergencyStopButton> {
  bool _inFlight = false;

  Future<void> _confirmAndStop() async {
    final confirmed = await showDialog<bool>(
      context: context,
      builder: (dialogContext) => AlertDialog(
        icon: Icon(Icons.warning_amber_rounded,
            color: Theme.of(dialogContext).colorScheme.error, size: 32),
        title: const Text('Stop everything now?'),
        content: const Text(
          'This aborts the running sequence and the in-flight exposure, '
          'stops guiding, parks the mount, and switches the flat panel '
          'light off. It cannot be undone — the sequence does not resume.',
        ),
        actions: [
          TextButton(
            onPressed: () => Navigator.of(dialogContext).pop(false),
            child: const Text('Cancel'),
          ),
          FilledButton(
            style: FilledButton.styleFrom(
              backgroundColor: Theme.of(dialogContext).colorScheme.error,
            ),
            onPressed: () => Navigator.of(dialogContext).pop(true),
            child: const Text('EMERGENCY STOP'),
          ),
        ],
      ),
    );
    if (confirmed != true || !mounted) return;

    final server = ref.read(activeServerProvider);
    final messenger = ScaffoldMessenger.of(context);
    if (server == null) {
      messenger.showSnackBar(
          const SnackBar(content: Text('No server connected — nothing to stop.')));
      return;
    }
    setState(() => _inFlight = true);
    try {
      final result =
          await ref.read(serverApiFactoryProvider)(server).emergencyStop();
      messenger.showSnackBar(SnackBar(
        duration: const Duration(seconds: 10),
        content: Text(_summarize(result)),
      ));
    } catch (e) {
      messenger.showSnackBar(SnackBar(
        duration: const Duration(seconds: 10),
        content: Text(
            'EMERGENCY STOP FAILED — '
            '${friendlyError(e, action: 'reach the rig')} '
            'Check your rig manually.'),
      ));
    } finally {
      if (mounted) setState(() => _inFlight = false);
    }
  }

  /// Wire tokens → operator-facing failure phrases. Every attempted-but-
  /// failed rung is loud — a hung guider that kept correcting is exactly as
  /// dangerous as an unparked mount, so no rung fails silently.
  static const _failurePhrases = <String, String>{
    'abort_runs': 'SEQUENCE ABORT FAILED (the run may still be executing)',
    'abort_exposure': 'EXPOSURE ABORT FAILED',
    'stop_guiding': 'GUIDING MAY STILL BE ACTIVE (stop failed)',
    'park': 'MOUNT PARK FAILED — verify it manually',
    'flat_panel_light_off': 'FLAT PANEL LIGHT-OFF FAILED',
  };

  static String _summarize(EmergencyStopResult r) {
    if (r.alreadyInProgress) {
      return 'An emergency stop is already running — no second volley sent.';
    }
    final parts = <String>[
      r.runsAborted > 0
          ? '${r.runsAborted} sequence run(s) aborted'
          : 'no sequence was running',
      if (r.exposureAborted) 'exposure aborted',
      if (r.guidingStopped) 'guiding stopped',
      if (r.parkRequested) 'mount told to park',
      if (r.flatPanelLightOff) 'flat panel light off',
    ];
    final failures = r.failedRungs
        .map((rung) => _failurePhrases[rung] ?? '$rung FAILED')
        .toList(growable: false);
    final summary = 'Emergency stop executed: ${parts.join(', ')}.';
    return failures.isEmpty
        ? summary
        : '$summary CHECK THE RIG: ${failures.join('; ')}.';
  }

  @override
  Widget build(BuildContext context) {
    final red = Theme.of(context).colorScheme.error;
    final simple = widget.simple;
    final radius = simple ? 10.0 : 16.0;
    final button = FilledButton.icon(
      onPressed: _inFlight ? null : _confirmAndStop,
      icon: _inFlight
          ? SizedBox(
              width: simple ? 18 : 14,
              height: simple ? 18 : 14,
              child: const CircularProgressIndicator(strokeWidth: 2),
            )
          : Icon(Icons.report, size: simple ? 20 : 16),
      label: Text(_inFlight ? 'Stopping…' : 'Emergency Stop'),
      style: FilledButton.styleFrom(
        backgroundColor: red,
        foregroundColor: Theme.of(context).colorScheme.onError,
        padding: EdgeInsets.symmetric(
            horizontal: 18, vertical: simple ? 0 : 7),
        minimumSize: Size(0, simple ? 56 : 0),
        tapTargetSize: MaterialTapTargetSize.shrinkWrap,
        visualDensity: simple ? VisualDensity.standard : VisualDensity.compact,
        shape: RoundedRectangleBorder(
            borderRadius: BorderRadius.circular(radius)),
        textStyle: TextStyle(
            fontSize: simple ? 14 : 13, fontWeight: FontWeight.w600),
      ),
    );
    return Padding(
      padding: EdgeInsets.symmetric(horizontal: simple ? 0 : 8),
      // The ring: a 2 px dark gap, then 1 px of Fault Red around it.
      child: Container(
        padding: const EdgeInsets.all(2),
        decoration: BoxDecoration(
          color: simple ? AraColors.bgPrimary : AraColors.bgPanel,
          borderRadius: BorderRadius.circular(radius + 3),
          border: Border.all(color: red),
        ),
        child: button,
      ),
    );
  }
}
