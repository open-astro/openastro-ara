import 'dart:async';

import 'package:flutter/gestures.dart' show PointerDeviceKind, kPrimaryButton;
import 'package:flutter/material.dart';
import 'package:flutter/semantics.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../l10n/l10n.dart';
import '../services/server_api.dart';
import '../state/equipment/camera_state.dart';
import '../theme/ara_colors.dart';
import '../state/saved_server_state.dart';
import '../util/friendly_error.dart';
import '../state/simple_messages_state.dart';
import '../state/ws/client_session_state.dart';

/// §35.3 — the persistent big red button, always visible in the bottom
/// status bar. Press and hold for 2 s ("Hold to confirm", a fill sweeping
/// across) → the daemon aborts
/// the running sequence, aborts the in-flight exposure, stops guiding,
/// parks the mount, switches the flat panel light off and the camera's
/// cooler off. The snackbar
/// reports honestly what each rung did (a dead mount says so instead of
/// pretending it parked). Letting go early sends nothing. Keyboard and
/// screen-reader activation can't hold, so they get the confirm dialog
/// ("Stop everything now?") instead; Simple Mode's versions, which show no
/// popups, ask them to press again within 3 s (PRESS AGAIN).
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

  /// Simple Mode, keyboard or screen reader: pressed once, waiting for the
  /// second press.
  bool _armed = false;
  Timer? _disarm;

  /// How long the first press stays armed.
  static const _pressAgainWindow = Duration(seconds: 3);

  @override
  void dispose() {
    _hold.dispose();
    _disarm?.cancel();
    super.dispose();
  }

  void _pressDown(PointerDownEvent e) {
    // Only the primary button (a finger, a left click) arms the hold.
    if (e.kind == PointerDeviceKind.mouse && e.buttons != kPrimaryButton) {
      return;
    }
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
    if (widget.simple || widget.compact) {
      _pressAgain();
    } else {
      _confirmAndStop();
    }
  }

  void _pressAgain() {
    if (_armed) {
      _disarm?.cancel();
      setState(() => _armed = false);
      _stop();
      return;
    }
    setState(() => _armed = true);
    SemanticsService.sendAnnouncement(
      View.of(context),
      context.l10n.estopPressAgainAnnouncement,
      Directionality.of(context),
    );
    _disarm = Timer(_pressAgainWindow, () {
      if (mounted) setState(() => _armed = false);
    });
  }

  Future<void> _confirmAndStop() async {
    final l10n = context.l10n;
    final confirmed = await showDialog<bool>(
      context: context,
      builder: (dialogContext) => AlertDialog(
        icon: Icon(
          Icons.warning_amber_rounded,
          color: Theme.of(dialogContext).colorScheme.error,
          size: 32,
        ),
        title: Text(l10n.estopConfirmTitle),
        content: Text(l10n.estopConfirmBody),
        actions: [
          TextButton(
            onPressed: () => Navigator.of(dialogContext).pop(false),
            child: Text(l10n.estopCancel),
          ),
          FilledButton(
            style: FilledButton.styleFrom(
              backgroundColor: Theme.of(dialogContext).colorScheme.error,
            ),
            onPressed: () => Navigator.of(dialogContext).pop(true),
            child: Text(l10n.estopConfirm),
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
    final notify = userNotifier(context);
    final l10n = context.l10n;
    if (server == null) {
      notify(l10n.estopNoServer, error: true, snackBarRed: false);
      return;
    }
    setState(() => _inFlight = true);
    ref.read(localEmergencyStopProvider.notifier).pressed();
    try {
      final result = await ref
          .read(serverApiFactoryProvider)(server)
          .emergencyStop();
      // The cooler toggle polls every 15 s; show the stop's cooler-off now.
      if (ref.exists(cameraStatusProvider)) {
        unawaited(ref.read(cameraStatusProvider.notifier).refresh());
      }
      // A rung that failed keeps its pill up until it is read.
      notify(
        _summarize(
          l10n,
          result,
          failuresFirst: widget.simple || widget.compact,
        ),
        error: result.failedRungs.isNotEmpty,
        snackBarRed: false,
        snackBarDuration: const Duration(seconds: 10),
        // Red when a rung failed; else amber, done but the rig needs a look.
        // Either way it waits to be read (user, 2026-11-04).
        warning: true,
      );
    } catch (e) {
      notify(
        l10n.estopFailed(friendlyError(l10n, e, action: l10n.estopActionReach)),
        error: true,
        snackBarRed: false,
        snackBarDuration: const Duration(seconds: 10),
      );
    } finally {
      if (mounted) setState(() => _inFlight = false);
    }
  }

  /// Wire tokens → operator-facing failure phrases. Every attempted-but-
  /// failed rung is loud — a hung guider that kept correcting is exactly as
  /// dangerous as an unparked mount, so no rung fails silently.
  static String _failurePhrase(AppLocalizations l, String rung) =>
      switch (rung) {
        'abort_runs' => l.estopFailAbortRuns,
        'abort_exposure' => l.estopFailAbortExposure,
        'stop_guiding' => l.estopFailStopGuiding,
        'park' => l.estopFailPark,
        'flat_panel_light_off' => l.estopFailFlatPanel,
        'cooler_off' => l.estopFailCooler,
        _ => l.estopFailOther(rung),
      };

  /// [failuresFirst]: Simple Mode's message pill shows two lines, so a
  /// failed rung leads instead of trailing off past them.
  static String _summarize(
    AppLocalizations l,
    EmergencyStopResult r, {
    bool failuresFirst = false,
  }) {
    if (r.alreadyInProgress) {
      return l.estopAlreadyRunning;
    }
    final parts = <String>[
      r.runsAborted > 0 ? l.estopRunsAborted(r.runsAborted) : l.estopNoSequence,
      if (r.exposureAborted) l.estopExposureAborted,
      if (r.guidingStopped) l.estopGuidingStopped,
      if (r.parkRequested) l.estopParkRequested,
      if (r.flatPanelLightOff) l.estopFlatPanelOff,
      if (r.coolerOff) l.estopCoolerOff,
    ];
    final failures = r.failedRungs
        .map((rung) => _failurePhrase(l, rung))
        .toList(growable: false);
    final summary = l.estopSummary(parts.join(', '));
    if (failures.isEmpty) return summary;
    final check = l.estopCheckRig(failures.join('; '));
    return failuresFirst ? '$check $summary' : '$summary $check';
  }

  @override
  Widget build(BuildContext context) {
    final l10n = context.l10n;
    final compact = widget.compact;
    final simple = widget.simple || compact;
    final red = simple
        ? AraColors.emergencyStop
        : Theme.of(context).colorScheme.error;
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
          ? (_inFlight
                ? '…'
                : _holding
                ? l10n.estopCompactHold
                : _armed
                ? l10n.estopCompactPressAgain
                : l10n.estopCompactStop)
          : _inFlight
          ? l10n.estopStopping
          : _holding
          ? l10n.estopHoldToConfirm
          : _armed
          ? l10n.estopPressAgain
          : l10n.estopLabel,
      maxLines: _armed && compact ? 2 : 1,
      textAlign: TextAlign.center,
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
      // From the theme's label style, so it carries the app's font family.
      textStyle: Theme.of(context).textTheme.labelLarge?.copyWith(
        fontSize: compact ? 12 : (simple ? 14 : 13),
        fontWeight: compact ? FontWeight.w700 : FontWeight.w600,
      ),
    );
    // Simple Mode stacks the icon over the label: the 60 px compact square
    // has no room for both on one line.
    final button = simple
        ? FilledButton(
            onPressed: _inFlight ? null : _onPressed,
            style: style,
            // Armed, the compact square drops the icon: PRESS AGAIN needs
            // both lines' room at larger text.
            child: Column(
              mainAxisSize: MainAxisSize.min,
              children: [
                if (!(_armed && compact)) ...[icon, const SizedBox(height: 2)],
                label,
              ],
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
      // Simple Mode's ring: a 2 px dark gap, then 1 px of Fault Red around it
      // (the full UI keeps its plain button; the hold and its sweep are both).
      child: Container(
        padding: EdgeInsets.all(simple ? 2 : 0),
        decoration: simple
            ? BoxDecoration(
                color: AraColors.bgPrimary,
                borderRadius: BorderRadius.circular(radius + 3),
                border: Border.all(color: red),
              )
            : null,
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
                        // Simple Mode's sweep darkens, keeping HOLD at 7.7:1;
                        // the full UI's lightens.
                        child: ColoredBox(
                          color: simple
                              ? const Color(0x40000000)
                              : const Color(0x40FFFFFF),
                        ),
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

/// When Emergency Stop was last pressed on this device: Simple Mode doesn't
/// sound the safety alarm for a stop its user just pressed (the stop's own
/// summary pill says what happened).
class LocalEmergencyStopNotifier extends Notifier<DateTime?> {
  @override
  DateTime? build() => null;

  void pressed() => state = DateTime.now();

  /// Pressed here within the last minute.
  bool get recent =>
      state != null &&
      DateTime.now().difference(state!) < const Duration(minutes: 1);
}

final localEmergencyStopProvider =
    NotifierProvider<LocalEmergencyStopNotifier, DateTime?>(
      LocalEmergencyStopNotifier.new,
    );
