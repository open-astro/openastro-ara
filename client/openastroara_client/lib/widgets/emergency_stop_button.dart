import 'dart:async';
import 'dart:math' as math;

import 'package:flutter/gestures.dart'
    show PointerDeviceKind, kLongPressTimeout, kPrimaryButton;
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
import 'adaptive_dialog.dart';
import 'adaptive_controls.dart';

/// §35.3 — the persistent big red button, always visible in the bottom
/// status bar. Press and hold for 2 s ("Hold to confirm", a fill sweeping
/// across) → the daemon aborts
/// the running sequence, stops the mount where it is with tracking off (never a park),
/// halts the rotator, aborts the in-flight exposure, stops the guide-camera focus loop,
/// the Bahtinov readout, autofocus and polar alignment, stops guiding, switches the
/// flat panel light off and the camera's cooler off. The snackbar
/// reports honestly what each rung did (a dead mount says so instead of
/// pretending it stopped). Letting go early sends nothing. Keyboard and
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
    this.large = false,
    this.railFoot = false,
    this.enabled = true,
    this.label,
  });

  /// Replaces the idle "STOP" word (the safety alarm's "Emergency Abort")
  /// and lets the label wrap to two lines; the hold, its fill and the stop
  /// itself are unchanged.
  final String? label;

  final bool simple;

  /// Simple Mode's rail version: a 60 px square reading STOP (HOLD while
  /// held), same hold-to-confirm.
  final bool compact;

  /// Monitor's corner version (#1724): the compact square at 108 pt, with
  /// type and icon to match.
  final bool large;

  /// Simple Mode's rail-foot Stop (#1911): the last row of the right rail on
  /// desktop and iPad, no red block. A tap sends nothing; the shared hold
  /// ([EmergencyStopHold]) is the stop, and keyboard and screen-reader
  /// activation press twice within 3 s, as the compact square does.
  final bool railFoot;

  /// False: drawn disabled in its place (a phone before any rig has been
  /// connected, #1775).
  final bool enabled;

  @override
  ConsumerState<EmergencyStopButton> createState() =>
      _EmergencyStopButtonState();
}

/// How long the press has to be held.
const kEmergencyStopHold = Duration(seconds: 2);

class _EmergencyStopButtonState extends ConsumerState<EmergencyStopButton>
    with SingleTickerProviderStateMixin {
  /// A stop is on its way, from this button or a shutter's hold.
  bool get _inFlight => ref.read(emergencyStopInFlightProvider);

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
    if (!widget.enabled) return;
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
    if (widget.simple || widget.compact || widget.large) {
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
    final confirmed = await showAdaptiveAlert<bool>(
      context: context,
      builder: (dialogContext) => adaptiveAlertDialog(
        icon: Icon(
          Icons.warning_amber_rounded,
          color: AraColors.accentError,
          size: 32,
        ),
        title: Text(l10n.estopConfirmTitle),
        content: Text(l10n.estopConfirmBody),
        actions: [
          adaptiveDialogAction(
            dialogContext,
            onPressed: () => Navigator.of(dialogContext).pop(false),
            child: Text(l10n.estopCancel),
          ),
          // Stop stays the default action on both platforms.
          adaptiveDialogAction(
            dialogContext,
            filled: true,
            isDefault: true,
            isDestructive: true,
            onPressed: () => Navigator.of(dialogContext).pop(true),
            child: Text(l10n.estopConfirm),
          ),
        ],
      ),
    );
    if (confirmed != true || !mounted) return;
    await _stop();
  }

  Future<void> _stop() => emergencyStopNow(
    context,
    failuresFirst: widget.simple || widget.compact || widget.large,
  );

  /// The rail-foot Stop: a hairline over an octagon and the word, in Fault
  /// Red's light tone. The shape and the word carry it, not the colour, so
  /// Night mode needs no palette of its own (plan OPEN CHOICE 6).
  Widget _buildRailFoot(BuildContext context) {
    final l10n = context.l10n;
    final scale = math.max(
      1.0,
      MediaQuery.textScalerOf(context).scale(1) / 1.1,
    );
    final inFlight = _inFlight;
    final word = inFlight
        ? '…'
        : _armed
        ? l10n.estopCompactPressAgain
        : l10n.estopCompactStop;
    final tone = widget.enabled ? AraColors.errorText : AraColors.textDisabled;
    const radius = BorderRadius.vertical(bottom: Radius.circular(10));
    return Semantics(
      button: true,
      enabled: widget.enabled && !inFlight,
      label: l10n.estopCompactStop,
      child: EmergencyStopHold(
        enabled: widget.enabled && !inFlight,
        borderRadius: radius,
        onActivate: widget.enabled && !inFlight ? _pressAgain : null,
        child: Container(
          key: const Key('simple-rail-stop'),
          height: 60 * scale,
          decoration: const BoxDecoration(
            borderRadius: radius,
            border: Border(top: BorderSide(color: AraColors.border)),
          ),
          alignment: Alignment.center,
          child: Column(
            mainAxisSize: MainAxisSize.min,
            children: [
              if (inFlight)
                const SizedBox(
                  width: 22,
                  height: 22,
                  child: AdaptiveProgressIndicator(strokeWidth: 2),
                )
              else if (!_armed)
                // Armed, the foot drops the icon: PRESS AGAIN needs both lines.
                Icon(Icons.report, size: 26, color: tone),
              Text(
                word,
                maxLines: _armed ? 2 : 1,
                textAlign: TextAlign.center,
                style: Theme.of(context).textTheme.labelLarge?.copyWith(
                  fontSize: 11.5,
                  fontWeight: FontWeight.w600,
                  letterSpacing: 0.4,
                  color: tone,
                ),
              ),
            ],
          ),
        ),
      ),
    );
  }

  @override
  Widget build(BuildContext context) {
    final l10n = context.l10n;
    ref.watch(emergencyStopInFlightProvider);
    if (widget.railFoot) return _buildRailFoot(context);
    final large = widget.large;
    final compact = widget.compact || large;
    final simple = widget.simple || compact;
    const red = AraColors.emergencyStop;
    final radius = simple ? 10.0 : 16.0;
    final icon = _inFlight
        ? SizedBox(
            width: simple ? 18 : 14,
            height: simple ? 18 : 14,
            child: const AdaptiveProgressIndicator(strokeWidth: 2),
          )
        : Icon(Icons.report, size: large ? 32 : (simple ? 20 : 16));
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
          : (widget.label ?? l10n.estopLabel),
      // A caller's own label (the alarm's longer words) may wrap to two lines.
      maxLines: _armed && compact || widget.label != null ? 2 : 1,
      textAlign: TextAlign.center,
    );
    final style = FilledButton.styleFrom(
      backgroundColor: red,
      // Not onError: the app's dark scheme makes that black (4.2:1 on the red).
      foregroundColor: AraColors.onEmergencyStop,
      // Drawn disabled (#1775), STOP still reads as STOP: a dim red with the
      // icon and word in grey, not the theme's near-invisible default.
      disabledBackgroundColor: widget.enabled
          ? null
          : red.withValues(alpha: 0.24),
      disabledForegroundColor: widget.enabled ? null : AraColors.textSecondary,
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
        fontSize: large ? 22 : (compact ? 12 : (simple ? 14 : 13)),
        fontWeight: compact ? FontWeight.w700 : FontWeight.w600,
      ),
    );
    // Simple Mode stacks the icon over the label: the 60 px compact square
    // has no room for both on one line.
    final button = simple
        ? FilledButton(
            onPressed: _inFlight || !widget.enabled ? null : _onPressed,
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
            onPressed: _inFlight || !widget.enabled ? null : _onPressed,
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
                        // Darkens, keeping HOLD at 7.7:1.
                        child: const ColoredBox(color: AraColors.holdFill),
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

/// A stop is on its way: one at a time, whichever control sent it.
class _StopInFlight extends Notifier<bool> {
  @override
  bool build() => false;

  void set(bool value) => state = value;
}

final emergencyStopInFlightProvider = NotifierProvider<_StopInFlight, bool>(
  _StopInFlight.new,
);

/// The one Emergency Stop path (ADR 0009 rule 3): the status-bar button, the
/// rail-foot Stop and every Simple Mode shutter's 2 s hold all end here, and
/// nothing else calls the daemon's emergency-stop route. It shows no
/// plan-running question (ADR 0013 rule 2). [failuresFirst]: Simple Mode's
/// message pill shows two lines, so a failed rung leads.
Future<void> emergencyStopNow(
  BuildContext context, {
  bool failuresFirst = true,
}) async {
  final container = ProviderScope.containerOf(context, listen: false);
  if (container.read(emergencyStopInFlightProvider)) return;
  final server = container.read(activeServerProvider);
  final notify = userNotifier(context);
  final l10n = context.l10n;
  if (server == null) {
    notify(l10n.estopNoServer, error: true, snackBarRed: false);
    return;
  }
  container.read(emergencyStopInFlightProvider.notifier).set(true);
  container.read(localEmergencyStopProvider.notifier).pressed();
  try {
    final result = await container
        .read(serverApiFactoryProvider)(server)
        .emergencyStop();
    // The cooler toggle polls every 15 s; show the stop's cooler-off now.
    if (container.read(cameraStatusProvider.exists)) {
      unawaited(container.read(cameraStatusProvider.notifier).refresh());
    }
    // A rung that failed keeps its pill up until it is read.
    notify(
      _summarize(l10n, result, failuresFirst: failuresFirst),
      error: result.failedRungs.isNotEmpty,
      snackBarRed: false,
      snackBarDuration: const Duration(seconds: 10),
      // Red when a rung failed; else amber, done but the rig needs a look.
      // Either way it waits to be read (user, 2026-11-04).
      warning: true,
      sticky: true,
    );
  } catch (e) {
    notify(
      l10n.estopFailed(friendlyError(l10n, e, action: l10n.estopActionReach)),
      error: true,
      snackBarRed: false,
      snackBarDuration: const Duration(seconds: 10),
      // The stop did NOT happen: it waits to be read (ALP-5016).
      sticky: true,
    );
  } finally {
    container.read(emergencyStopInFlightProvider.notifier).set(false);
  }
}

/// Wire tokens → operator-facing failure phrases. Every attempted-but-
/// failed rung is loud — a hung guider that kept correcting is exactly as
/// dangerous as a mount still moving, so no rung fails silently.
String _failurePhrase(AppLocalizations l, String rung) => switch (rung) {
  'abort_runs' => l.estopFailAbortRuns,
  'abort_exposure' => l.estopFailAbortExposure,
  'stop_guide_focus' => l.estopFailGuideFocus,
  'stop_bahtinov' => l.estopFailBahtinov,
  'cancel_autofocus' => l.estopFailAutofocus,
  'stop_polar_align' => l.estopFailPolarAlign,
  'stop_polar_aligner' => l.estopFailPolarAligner,
  'stop_guiding' => l.estopFailStopGuiding,
  'stop_mount' => l.estopFailStopMount,
  'halt_rotator' => l.estopFailRotator,
  'flat_panel_light_off' => l.estopFailFlatPanel,
  'cooler_off' => l.estopFailCooler,
  _ => l.estopFailOther(rung),
};

/// [failuresFirst]: Simple Mode's message pill shows two lines, so a
/// failed rung leads instead of trailing off past them.
String _summarize(
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
    if (r.mountStopped) l.estopMountStopped,
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

/// The hold every Simple Mode shutter and the rail-foot Stop share (#1911).
/// The timer starts at pointer down. A release before [kLongPressTimeout]
/// (500 ms) is a tap and runs [onTap]; the red fill and HOLD start at 500 ms;
/// 2 s from pointer down fires one Emergency Stop through [emergencyStopNow].
/// A release or a slide off the control after the fill started and before
/// 2 s cancels and fires nothing, and is no tap. [onActivate] is what a
/// keyboard's Enter or a screen reader's activate runs (default [onTap]): it
/// never stops anything by itself.
class EmergencyStopHold extends ConsumerStatefulWidget {
  const EmergencyStopHold({
    super.key,
    required this.child,
    this.onTap,
    this.onActivate,
    this.enabled = true,
    this.borderRadius = const BorderRadius.all(Radius.circular(30)),
    this.inkRadius = 36,
  });

  final Widget child;

  /// A pointer tap: a release before 500 ms.
  final VoidCallback? onTap;

  /// Keyboard and screen-reader activation; [onTap] when null.
  final VoidCallback? onActivate;

  /// False: no hold arms (a stop is already on its way).
  final bool enabled;

  /// The shape the fill is clipped to: a 60 px circle by default.
  final BorderRadius borderRadius;
  final double inkRadius;

  @override
  ConsumerState<EmergencyStopHold> createState() => _EmergencyStopHoldState();
}

class _EmergencyStopHoldState extends ConsumerState<EmergencyStopHold>
    with SingleTickerProviderStateMixin {
  /// The fill's sweep, from 500 ms to 2 s after pointer down.
  late final AnimationController _fill = AnimationController(
    vsync: this,
    duration: kEmergencyStopHold - kLongPressTimeout,
  );
  Timer? _fillTimer;
  Timer? _fireTimer;

  /// A press is down and counting.
  bool _down = false;

  /// 500 ms passed: the red fill and HOLD show.
  bool _filling = false;

  /// The press came from a pointer, which the [Listener] handled; the
  /// [InkResponse] tap is for the keyboard and screen readers only.
  bool _viaPointer = false;

  @override
  void dispose() {
    _fillTimer?.cancel();
    _fireTimer?.cancel();
    _fill.dispose();
    super.dispose();
  }

  void _pointerDown(PointerDownEvent e) {
    _viaPointer = true;
    // Only a finger or the main mouse button, as the Emergency Stop.
    if (e.kind == PointerDeviceKind.mouse && e.buttons != kPrimaryButton) {
      return;
    }
    if (_down || !widget.enabled) return;
    _down = true;
    _fillTimer = Timer(kLongPressTimeout, () {
      _filling = true;
      _fill.forward(from: 0);
      setState(() {});
    });
    _fireTimer = Timer(kEmergencyStopHold, () {
      _reset();
      emergencyStopNow(context);
    });
  }

  void _pointerMove(PointerMoveEvent e) {
    if (!_down) return;
    final size = context.size;
    if (size != null && !(Offset.zero & size).contains(e.localPosition)) {
      _reset();
    }
  }

  void _pointerUp(PointerUpEvent _) {
    scheduleMicrotask(() => _viaPointer = false);
    if (!_down) return;
    final tap = !_filling;
    _reset();
    if (tap) widget.onTap?.call();
  }

  void _pointerCancel(PointerCancelEvent _) {
    scheduleMicrotask(() => _viaPointer = false);
    _reset();
  }

  /// Ends the press: the timers stop and the fill goes. A press that fired
  /// leaves [_down] false, so its release is no tap.
  void _reset() {
    _fillTimer?.cancel();
    _fireTimer?.cancel();
    _down = false;
    if (_filling) {
      _filling = false;
      _fill.value = 0;
      if (mounted) setState(() {});
    }
  }

  @override
  Widget build(BuildContext context) {
    final still = MediaQuery.disableAnimationsOf(context);
    return Listener(
      onPointerDown: _pointerDown,
      onPointerMove: _pointerMove,
      onPointerUp: _pointerUp,
      onPointerCancel: _pointerCancel,
      child: Stack(
        clipBehavior: Clip.none,
        children: [
          InkResponse(
            radius: widget.inkRadius,
            onTap: widget.onTap == null && widget.onActivate == null
                ? null
                : () {
                    if (!_viaPointer) {
                      (widget.onActivate ?? widget.onTap)?.call();
                    }
                  },
            child: widget.child,
          ),
          if (_filling)
            Positioned.fill(
              child: IgnorePointer(
                child: ClipRRect(
                  key: const Key('emergency-hold-fill'),
                  borderRadius: widget.borderRadius,
                  child: AnimatedBuilder(
                    animation: _fill,
                    builder: (context, _) => CustomPaint(
                      painter: _HoldSweep(
                        // Reduced motion: four steps, not a smooth sweep.
                        still ? (_fill.value * 4).ceil() / 4 : _fill.value,
                      ),
                      child: Center(
                        child: Text(
                          context.l10n.estopCompactHold,
                          style: const TextStyle(
                            color: AraColors.onEmergencyStop,
                            fontSize: 11,
                            fontWeight: FontWeight.w700,
                            letterSpacing: 0.4,
                          ),
                        ),
                      ),
                    ),
                  ),
                ),
              ),
            ),
        ],
      ),
    );
  }
}

/// The hold's progress: Fault Red sweeping clockwise from twelve o'clock over
/// a dark red, so the word HOLD reads on both.
class _HoldSweep extends CustomPainter {
  const _HoldSweep(this.progress);

  final double progress;

  @override
  void paint(Canvas canvas, Size size) {
    canvas.drawRect(Offset.zero & size, Paint()..color = AraColors.holdBase);
    final centre = size.center(Offset.zero);
    final reach = size.longestSide;
    canvas.drawArc(
      Rect.fromCircle(center: centre, radius: reach),
      -math.pi / 2,
      2 * math.pi * progress,
      true,
      Paint()..color = AraColors.emergencyStop,
    );
  }

  @override
  bool shouldRepaint(_HoldSweep old) => old.progress != progress;
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
