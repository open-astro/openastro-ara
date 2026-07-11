import 'dart:async';

import 'package:flutter/material.dart';
import 'package:flutter/services.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../state/equipment/mount_state.dart';
import '../state/sequencer/sequence_list_state.dart';
import '../state/ws/ws_providers.dart';
import '../theme/ara_colors.dart';

/// §57.8 telescope slew lifecycle WS tokens (mirrors `WsEventCatalog`).
abstract final class SlewWsEvents {
  static const started = 'telescope.slew_started';
  static const complete = 'telescope.slew_complete';
  static const aborted = 'telescope.slew_aborted';
}

/// §57.2/57.3/57.4 — the Stop Mount panic button. Wraps the app shell's body:
/// while the daemon reports an autonomous slew in progress (any source —
/// sequencer, meridian flip, recovery, park/home, REST), a prominent red
/// overlay button surfaces near the top of the view; one tap (or Space on
/// desktop, §57.3) issues the panic stop, and the daemon's `slew_aborted`
/// event opens the post-stop modal (§57.4: halted position + Resume / Skip
/// target / End session against the run the daemon paused).
class StopMountListener extends ConsumerStatefulWidget {
  final Widget child;

  const StopMountListener({super.key, required this.child});

  @override
  ConsumerState<StopMountListener> createState() => _StopMountListenerState();
}

class _StopMountListenerState extends ConsumerState<StopMountListener> {
  bool _slewing = false;
  double? _targetRa;
  double? _targetDec;
  bool _stopRequested = false;

  /// The run the daemon paused for this stop (§57.4 step 2) — stamped by the
  /// `sequence.paused` event that follows the abort (gate-arm: it can arrive
  /// seconds after the modal opens; the modal reads this at press time).
  String? _pausedRunId;
  bool _awaitingPause = false;

  @override
  Widget build(BuildContext context) {
    ref.listen(wsEventsProvider, (previous, next) {
      final event = next.asData?.value;
      if (event == null) return;
      switch (event.type) {
        case SlewWsEvents.started:
          setState(() {
            _slewing = true;
            _stopRequested = false;
            _targetRa = _numOrNull(event.payload['target_ra_hours']);
            _targetDec = _numOrNull(event.payload['target_dec_degrees']);
          });
        case SlewWsEvents.complete:
          setState(() => _slewing = false);
        case SlewWsEvents.aborted:
          setState(() => _slewing = false);
          // Arm the paused-run capture HERE, not in _stopMount (#837 r1): the
          // abort may not be ours — another connected client, or the server
          // itself. Clearing the previous cycle's id also stops a stale run
          // from being resumed/skipped by mistake. The daemon always publishes
          // slew_aborted before the follow-up sequence.paused (the pause is
          // requested after the abort and lands at an instruction boundary).
          _pausedRunId = null;
          _awaitingPause = true;
          _showStoppedModal(
            haltedRa: _numOrNull(event.payload['halted_ra_hours']),
            haltedDec: _numOrNull(event.payload['halted_dec_degrees']),
          );
        case SequenceWsEvents.paused:
          if (_awaitingPause && event.payload['sequence_id'] is String) {
            _pausedRunId = event.payload['sequence_id'] as String;
          }
      }
    });
    // A dropped link means the slew state is unknown — don't strand a stale
    // panic button over the reconnect flow.
    ref.listen(serverLinkUpProvider, (previous, next) {
      if (!next && _slewing) {
        setState(() => _slewing = false);
      }
    });

    if (!_slewing) return widget.child;
    return CallbackShortcuts(
      // §57.3 — Space is the desktop panic key while (and only while) a slew
      // is in progress; a focused text field consumes Space first, which is
      // the right precedence.
      bindings: {
        const SingleActivator(LogicalKeyboardKey.space): _stopMount,
      },
      child: Stack(
        // Fill the parent regardless of the child's own size, so the banner's
        // hit region is never clipped to a smaller content box.
        fit: StackFit.expand,
        children: [
          widget.child,
          Positioned(
            top: 0,
            left: 0,
            right: 0,
            // The listener wraps the shell's SafeArea, so honor the top inset
            // here: at least 64 px down, more when a notch/status bar demands
            // it (#837 r1).
            child: SafeArea(
              bottom: false,
              minimum: const EdgeInsets.only(top: 64),
              child: Center(child: _slewBanner(context)),
            ),
          ),
        ],
      ),
    );
  }

  Widget _slewBanner(BuildContext context) {
    final theme = Theme.of(context);
    final target = _targetRa != null && _targetDec != null
        ? ' to RA ${_targetRa!.toStringAsFixed(2)}h, '
              'Dec ${_targetDec! >= 0 ? '+' : ''}${_targetDec!.toStringAsFixed(1)}°'
        : '';
    return Material(
      key: const ValueKey('stop_mount_banner'),
      elevation: 8,
      borderRadius: BorderRadius.circular(12),
      color: AraColors.bgPanel,
      child: Padding(
        padding: const EdgeInsets.all(16),
        child: Column(
          mainAxisSize: MainAxisSize.min,
          children: [
            Text('Mount is slewing$target', style: theme.textTheme.bodyMedium),
            const SizedBox(height: 12),
            ConstrainedBox(
              // §57.3 — sized for panic-press.
              constraints: const BoxConstraints(minWidth: 200, minHeight: 80),
              child: FilledButton(
                key: const ValueKey('stop_mount_button'),
                style: FilledButton.styleFrom(
                  backgroundColor: AraColors.accentError,
                  foregroundColor: Colors.white,
                  textStyle: theme.textTheme.titleLarge
                      ?.copyWith(fontWeight: FontWeight.bold),
                ),
                // Single-tap, no confirmation gate — the button IS the
                // confirmation (§57.3).
                onPressed: _stopRequested ? null : _stopMount,
                child: Text(_stopRequested ? 'STOPPING…' : '⛔ STOP MOUNT'),
              ),
            ),
          ],
        ),
      ),
    );
  }

  Future<void> _stopMount() async {
    if (_stopRequested || !_slewing) return;
    setState(() => _stopRequested = true);
    final ok = await ref.read(mountProvider.notifier).abortSlew();
    if (!mounted) return;
    if (!ok) {
      setState(() => _stopRequested = false);
      ScaffoldMessenger.of(context).showSnackBar(
        const SnackBar(
          content: Text(
            'Stop Mount failed to reach the server — check the connection '
            '(the sequence is still paused if the request landed).',
          ),
        ),
      );
    }
    // On success the daemon's slew_aborted event closes the overlay and
    // opens the post-stop modal — no optimistic hide, the event is truth.
  }

  void _showStoppedModal({double? haltedRa, double? haltedDec}) {
    unawaited(
      showDialog<void>(
        context: context,
        builder: (dialogContext) => _MountStoppedDialog(
          haltedRa: haltedRa,
          haltedDec: haltedDec,
          onAction: (action) => _runSequenceAction(action),
        ),
      ).whenComplete(() => _awaitingPause = false),
    );
  }

  Future<void> _runSequenceAction(_StopModalAction action) async {
    final api = ref.read(sequenceApiProvider);
    final id = _pausedRunId;
    final messenger = ScaffoldMessenger.of(context);
    if (api == null || id == null) {
      messenger.showSnackBar(
        const SnackBar(
          content: Text(
            'No paused run to act on — nothing was running, or the pause '
            'has not landed yet. Use the Run tab for sequence control.',
          ),
        ),
      );
      return;
    }
    try {
      switch (action) {
        case _StopModalAction.resume:
          await api.resume(id);
        case _StopModalAction.skipTarget:
          // §57.4 "Skip this target": skip what the run is executing now,
          // then let it continue with the rest of the plan.
          await api.skipCurrent(id);
          await api.resume(id);
        case _StopModalAction.endSession:
          await api.stop(id);
      }
    } catch (e) {
      if (!mounted) return;
      messenger.showSnackBar(
        SnackBar(content: Text('Sequence action failed: $e')),
      );
    }
  }

  static double? _numOrNull(dynamic v) => v is num ? v.toDouble() : null;
}

enum _StopModalAction { resume, skipTarget, endSession }

/// §57.4 — the post-stop modal: where the mount halted, what was paused, and
/// the resume/skip/end choices. Verify-Position (capture + solve) is the
/// deferred follow-up.
class _MountStoppedDialog extends StatelessWidget {
  final double? haltedRa;
  final double? haltedDec;
  final void Function(_StopModalAction action) onAction;

  const _MountStoppedDialog({
    required this.haltedRa,
    required this.haltedDec,
    required this.onAction,
  });

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);
    final position = haltedRa != null && haltedDec != null
        ? 'RA ${haltedRa!.toStringAsFixed(2)}h, '
              'Dec ${haltedDec! >= 0 ? '+' : ''}${haltedDec!.toStringAsFixed(1)}°'
        : 'unknown';
    return AlertDialog(
      title: const Text('Mount stopped at user request'),
      content: Column(
        mainAxisSize: MainAxisSize.min,
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          Text('Current position: $position'),
          const SizedBox(height: 8),
          Text(
            'Any running sequence pauses at its next instruction boundary. '
            'Cooler, guider, and other equipment are still running. Verify '
            'the mount is in a safe position before resuming.',
            style: theme.textTheme.bodySmall,
          ),
        ],
      ),
      actions: [
        TextButton(
          key: const ValueKey('stop_modal_resume'),
          onPressed: () {
            Navigator.of(context).pop();
            onAction(_StopModalAction.resume);
          },
          child: const Text('Resume sequence'),
        ),
        TextButton(
          key: const ValueKey('stop_modal_skip'),
          onPressed: () {
            Navigator.of(context).pop();
            onAction(_StopModalAction.skipTarget);
          },
          child: const Text('Skip this target'),
        ),
        TextButton(
          key: const ValueKey('stop_modal_end'),
          onPressed: () {
            Navigator.of(context).pop();
            onAction(_StopModalAction.endSession);
          },
          child: const Text('End session'),
        ),
        FilledButton(
          key: const ValueKey('stop_modal_close'),
          onPressed: () => Navigator.of(context).pop(),
          child: const Text('Close'),
        ),
      ],
    );
  }
}
