import 'dart:async';

import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../services/server_api.dart';
import '../theme/ara_colors.dart';
import '../state/saved_server_state.dart';
import '../util/friendly_error.dart';
import '../state/ws/client_session_state.dart';

/// §35.3 — the persistent big red button, always visible in the bottom
/// status bar. Press and hold for 2 s ("Hold to confirm", a fill sweeping
/// across) → the daemon aborts
/// the running sequence, aborts the in-flight exposure, stops guiding,
/// parks the mount, and switches the flat panel light off. The snackbar
/// reports honestly what each rung did (a dead mount says so instead of
/// pretending it parked). Letting go early sends nothing. Keyboard and
/// screen-reader activation can't hold, so they get the confirm dialog
/// ("Stop everything now?") instead.
///
/// Heavier than any other control (ALP-4812): a ring of Fault Red set off by
/// a dark gap. [simple] is Simple Mode's version — 56 px tall, 10 px corners,
/// 14 px text — matching the design preview.
class EmergencyStopButton extends ConsumerStatefulWidget {
  const EmergencyStopButton({
    super.key,
    this.simple = false,
    this.compact = false,
  });

  final bool simple;

  /// Simple Mode's rail version: a 60 px square reading STOP (HOLD while
  /// held), same hold-to-confirm.
  final bool compact;

  @override
  ConsumerState<EmergencyStopButton> createState() =>
      _EmergencyStopButtonState();
}

/// How long the press has to be held.
const kEmergencyStopHold = Duration(seconds: 2);

class _EmergencyStopButtonState extends ConsumerState<EmergencyStopButton>
    with SingleTickerProviderStateMixin {
  bool _inFlight = false;

  /// Runs while the button is held; reaching the end is the confirmation.
  late final AnimationController _hold =
      AnimationController(vsync: this, duration: kEmergencyStopHold)
        ..addStatusListener((status) {
          if (status == AnimationStatus.completed) {
            _hold.value = 0;
            setState(() => _holding = false);
            _stop();
          }
        });

  /// Set by a pointer press so the button's own onPressed (which fires on
  /// release) doesn't also open the dialog meant for keyboard and screen
  /// readers.
  bool _viaPointer = false;

  /// The press is down and counting: the label reads "Hold to confirm".
  bool _holding = false;

  @override
  void dispose() {
    _hold.dispose();
    super.dispose();
  }

  void _pressDown(PointerDownEvent _) {
    _viaPointer = true;
    if (_inFlight) return;
    setState(() => _holding = true);
    _hold.forward(from: 0);
  }

  void _pressEnd(PointerEvent _) {
    // A release short of 2 s sends nothing.
    if (_holding) {
      setState(() => _holding = false);
      _hold.reverse();
    }
    // onPressed (if the release counts as a tap) runs in this same dispatch.
    scheduleMicrotask(() => _viaPointer = false);
  }

  void _onPressed() {
    if (_viaPointer) return;
    _confirmAndStop();
  }

  Future<void> _confirmAndStop() async {
    final confirmed = await showDialog<bool>(
      context: context,
      builder: (dialogContext) => AlertDialog(
        icon: Icon(
          Icons.warning_amber_rounded,
          color: Theme.of(dialogContext).colorScheme.error,
          size: 32,
        ),
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
    await _stop();
  }

  Future<void> _stop() async {
    if (_inFlight) return;
    final server = ref.read(activeServerProvider);
    final messenger = ScaffoldMessenger.of(context);
    if (server == null) {
      messenger.showSnackBar(
        const SnackBar(content: Text('No server connected — nothing to stop.')),
      );
      return;
    }
    setState(() => _inFlight = true);
    try {
      final result = await ref
          .read(serverApiFactoryProvider)(server)
          .emergencyStop();
      messenger.showSnackBar(
        SnackBar(
          duration: const Duration(seconds: 10),
          content: Text(_summarize(result)),
        ),
      );
    } catch (e) {
      messenger.showSnackBar(
        SnackBar(
          duration: const Duration(seconds: 10),
          content: Text(
            'EMERGENCY STOP FAILED — '
            '${friendlyError(e, action: 'reach the rig')} '
            'Check your rig manually.',
          ),
        ),
      );
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
    final compact = widget.compact;
    final simple = widget.simple || compact;
    final radius = simple ? 10.0 : 16.0;
    final icon = _inFlight
        ? SizedBox(
            width: simple ? 18 : 14,
            height: simple ? 18 : 14,
            child: const CircularProgressIndicator(strokeWidth: 2),
          )
        : Icon(Icons.report, size: simple ? 20 : 16);
    final label = Text(
      compact
          ? (_inFlight ? '…' : (_holding ? 'HOLD' : 'STOP'))
          : _inFlight
          ? 'Stopping…'
          : _holding
          ? 'Hold to confirm'
          : 'Emergency Stop',
      maxLines: 1,
    );
    final style = FilledButton.styleFrom(
      backgroundColor: red,
      foregroundColor: Theme.of(context).colorScheme.onError,
      padding: EdgeInsets.symmetric(
        horizontal: compact ? 0 : (simple ? 8 : 18),
        vertical: simple ? 0 : 7,
      ),
      minimumSize: Size(0, compact ? 54 : (simple ? 50 : 0)),
      tapTargetSize: MaterialTapTargetSize.shrinkWrap,
      visualDensity: simple ? VisualDensity.standard : VisualDensity.compact,
      shape: RoundedRectangleBorder(
        borderRadius: BorderRadius.circular(radius),
      ),
      textStyle: TextStyle(
        fontSize: compact ? 12 : (simple ? 14 : 13),
        fontWeight: compact ? FontWeight.w700 : FontWeight.w600,
      ),
    );
    // Simple Mode stacks the icon over the label: the 168 px column is too
    // narrow for both on one line without crowding them.
    final button = simple
        ? FilledButton(
            onPressed: _inFlight ? null : _onPressed,
            style: style,
            child: Column(
              mainAxisSize: MainAxisSize.min,
              children: [icon, const SizedBox(height: 2), label],
            ),
          )
        : FilledButton.icon(
            onPressed: _inFlight ? null : _onPressed,
            style: style,
            icon: icon,
            label: label,
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
        child: Listener(
          onPointerDown: _pressDown,
          onPointerUp: _pressEnd,
          onPointerCancel: _pressEnd,
          child: Stack(
            fit: StackFit.passthrough,
            children: [
              button,
              // The hold's progress, sweeping left to right over the red.
              Positioned.fill(
                child: IgnorePointer(
                  child: ClipRRect(
                    borderRadius: BorderRadius.circular(radius),
                    child: AnimatedBuilder(
                      animation: _hold,
                      builder: (context, _) => FractionallySizedBox(
                        alignment: Alignment.centerLeft,
                        widthFactor: _hold.value,
                        child: const ColoredBox(color: Color(0x40FFFFFF)),
                      ),
                    ),
                  ),
                ),
              ),
            ],
          ),
        ),
      ),
    );
  }
}
