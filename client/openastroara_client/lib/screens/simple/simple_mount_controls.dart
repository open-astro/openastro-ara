import 'dart:async';
import 'dart:math' as math;

import 'package:clock/clock.dart';
import 'package:flutter/material.dart';
import 'package:flutter/services.dart'
    show KeyDownEvent, KeyUpEvent, LogicalKeyboardKey;
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../l10n/l10n.dart';
import '../../models/mount_status.dart';
import '../../models/verify_position_result.dart';
import '../../services/center_api.dart';
import '../../services/plate_solve_api.dart';
import '../../services/autofocus_api.dart' show AutofocusJob;
import '../../state/app_locale_state.dart';
import '../../state/equipment/mount_state.dart';
import '../../state/imaging/exposure_state.dart';
import '../../state/plate_solve/plate_solve_api_state.dart';
import '../../state/settings/settings_nav.dart';
import '../../state/simple_messages_state.dart';
import '../../state/simple_run_state.dart';
import '../../theme/ara_colors.dart';
import '../../theme/ara_metrics.dart';
import '../../util/apparent_place.dart';
import '../../util/friendly_error.dart';
import '../../util/slew_rates.dart';
import '../../widgets/status_indicator.dart' show StatusBlink;
import 'simple_device_windows.dart' show SimpleWindowButton;
import 'simple_preview_panel.dart' show SimpleStopSquare, kSimpleStopSquare;
import 'simple_shell.dart' show SimpleSegments, simpleRailScale;

/// Simple Mode's Mount window controls for moving the mount by hand (#1649)
/// and checking where it points (#1650): the direction pad and the Pointing
/// row. The window (simple_device_windows.dart) lays them out.

/// How long a held direction's lease is, and how often it is renewed: the rig
/// stops the axis by itself about a second after the renewals stop.
const int kSimplePadLeaseMs = 1000;
const Duration kSimplePadRenew = Duration(milliseconds: 300);

/// One pad cell at 1x text, over Simple Mode's 44 pt touch minimum; it grows
/// with the text as the window does ([simpleRailScale]).
const double kSimplePadCell = 60;

/// The pad's speeds: Slow, Medium and Fast are 5 %, 25 % and 100 % of the
/// mount's top speed, each moved into the mount's rate bands (inside a band
/// as is, else to the nearest band edge, so a discrete-rate mount gets its own
/// steps), deduped, so a mount with fewer steps gets fewer names. (id, rate)
/// slowest first. Pure — unit-tested.
List<(String, double)> simplePadSpeeds(List<SlewRateBand> bands) {
  final usable = [
    for (final b in bands)
      if (b.max > 0) b,
  ];
  if (usable.isEmpty) return const [];
  final top = usable.map((b) => b.max).reduce(math.max);
  double fit(double want) {
    var best = 0.0, gap = double.infinity;
    for (final b in usable) {
      final r = want.clamp(b.min, b.max).toDouble();
      if ((r - want).abs() < gap) (best, gap) = (r, (r - want).abs());
    }
    return best;
  }

  final picks = <double>[];
  for (final want in [top * 0.05, top * 0.25, top]) {
    final r = fit(want);
    if (!picks.contains(r)) picks.add(r);
  }
  final ids = switch (picks.length) {
    1 => const ['fast'],
    2 => const ['slow', 'fast'],
    _ => const ['slow', 'medium', 'fast'],
  };
  return [for (var i = 0; i < picks.length; i++) (ids[i], picks[i])];
}

/// Why the pad's directions can't move the mount now, first match wins; null
/// when they can. A GoTo under way leaves Stop working; while the pad itself
/// moves the mount ([padMoving]) the mount's Slewing is the pad's own move
/// (ASCOM sets it during MoveAxis), not a GoTo. Pure — unit-tested.
String? simplePadBlocker(
  AppLocalizations l10n,
  MountStatus m, {
  required bool planImaging,
  required bool polarAligning,
  required bool guiding,
  bool padMoving = false,
}) {
  if (m.parked) return l10n.padUnparkToMove;
  if (planImaging) return l10n.padPausePlan;
  if (polarAligning) return l10n.padPolarRunning;
  if (guiding) return l10n.padStopGuiding;
  if (m.isBusy && !padMoving) return l10n.padSlewing;
  return null;
}

/// An offset as read in the window: arcseconds under 1′, arcminutes to one
/// place, degrees past a degree ("45″", "2.4′", "1.2°"). Pure — unit-tested.
String simpleSeparation(double arcmin) {
  if (arcmin < 1) {
    final arcsec = (arcmin * 60).round();
    if (arcsec < 60) return '$arcsec″';
  }
  if (arcmin < 59.95) return '${arcmin.toStringAsFixed(1)}′';
  return '${(arcmin / 60).toStringAsFixed(1)}°';
}

/// The queued Target the mount was sent to: the one within 1′ of [raDeg] /
/// [decDeg] (J2000), or null. Pure — unit-tested.
String? simplePointingTargetName(
  List<SimpleTarget> targets,
  double raDeg,
  double decDeg,
) {
  for (final t in targets) {
    if (_arcmin(t.raDeg, t.decDeg, raDeg, decDeg) <= 1) return t.name;
  }
  return null;
}

double _arcmin(double ra1, double dec1, double ra2, double dec2) {
  const r = math.pi / 180;
  final a = dec1 * r, b = dec2 * r;
  // Haversine: well behaved at the tiny separations compared here.
  final h =
      math.pow(math.sin((b - a) / 2), 2) +
      math.cos(a) * math.cos(b) * math.pow(math.sin((ra1 - ra2) * r / 2), 2);
  return 2 * math.asin(math.sqrt(h).clamp(0.0, 1.0)) / r * 60;
}

/// The pad moves the mount: from a press until a few seconds after it is let
/// go (the mount slows down after the stop). The window reads it so the
/// mount's Slewing during a pad move doesn't block the pad (#1649).
class SimplePadMovingNotifier extends Notifier<bool> {
  Timer? _settle;

  @override
  bool build() {
    ref.onDispose(() => _settle?.cancel());
    return false;
  }

  void pressed() {
    _settle?.cancel();
    state = true;
  }

  void released() {
    _settle?.cancel();
    _settle = Timer(const Duration(seconds: 3), () => state = false);
  }
}

final simplePadMovingProvider = NotifierProvider<SimplePadMovingNotifier, bool>(
  SimplePadMovingNotifier.new,
);

/// The eight directions as (axis, sign) moves, the full UI pad's mapping:
/// the primary axis (RA/azimuth) is W − / E +, the secondary (Dec/altitude)
/// S − / N +.
const _padMoves = <String, List<(int, int)>>{
  'nw': [(1, 1), (0, -1)],
  'n': [(1, 1)],
  'ne': [(1, 1), (0, 1)],
  'w': [(0, -1)],
  'e': [(0, 1)],
  'sw': [(1, -1), (0, -1)],
  's': [(1, -1)],
  'se': [(1, -1), (0, 1)],
};

/// The direction pad: eight directions around a centre Stop, and the speed
/// segments above it (or the reason it is off in their place, so the pad's
/// bottom edge stays put). Hold a direction to move, let go to stop; while
/// held the move is renewed on a short lease, so a lost link stops the mount
/// on the rig. Leaving the app or closing the window stops it too. The arrow
/// keys drive N/S/E/W while the window has focus, Escape stops.
class SimpleMountPad extends ConsumerStatefulWidget {
  const SimpleMountPad({
    super.key,
    required this.speeds,
    required this.blocker,
    this.moving = false,
    this.canHome = false,
    this.onHome,
  });

  /// From [simplePadSpeeds]; never empty (the window says the mount can't
  /// be moved by hand instead).
  final List<(String, double)> speeds;

  /// From [simplePadBlocker]: null, or why the directions are off.
  final String? blocker;

  /// The mount moves on its own (a Home, a GoTo, a park): the centre is Stop,
  /// the red square, which aborts every move.
  final bool moving;

  /// The mount can find home: the centre is Home while it is still. Without
  /// it the centre is always Stop.
  final bool canHome;

  /// Sends the mount home (the window asks first while a plan runs); null
  /// while it can't (parked).
  final VoidCallback? onHome;

  @override
  ConsumerState<SimpleMountPad> createState() => _SimpleMountPadState();
}

class _SimpleMountPadState extends ConsumerState<SimpleMountPad>
    with WidgetsBindingObserver {
  late String _speed = _defaultSpeed();
  String? _held;
  Timer? _renew;

  /// One refusal pill per press (a diagonal's two starts share it).
  bool _refusalShown = false;

  late final _focus = FocusNode(
    debugLabel: 'simple-mount-pad',
    onKeyEvent: _key,
  );

  /// Read on the first send: dispose may still need it to stop a held move,
  /// and ref is gone by then.
  late final MountNotifier _mount = ref.read(mountProvider.notifier);
  late final SimplePadMovingNotifier _moving = ref.read(
    simplePadMovingProvider.notifier,
  );

  String _defaultSpeed() {
    final ids = [for (final (id, _) in widget.speeds) id];
    return ids.contains('medium') ? 'medium' : ids[(ids.length - 1) ~/ 2];
  }

  double get _rate => widget.speeds
      .firstWhere((s) => s.$1 == _speed, orElse: () => widget.speeds.first)
      .$2;

  @override
  void initState() {
    super.initState();
    WidgetsBinding.instance.addObserver(this);
  }

  @override
  void didUpdateWidget(SimpleMountPad old) {
    super.didUpdateWidget(old);
    if (!widget.speeds.any((s) => s.$1 == _speed)) _speed = _defaultSpeed();
    // Blocked while held (a plan started, the mount parked): stop at once.
    if (widget.blocker != null && _held != null) _release();
  }

  @override
  void didChangeAppLifecycleState(AppLifecycleState state) {
    if (state != AppLifecycleState.resumed && _held != null) _release();
  }

  @override
  void dispose() {
    WidgetsBinding.instance.removeObserver(this);
    _renew?.cancel();
    if (_held case final dir?) {
      for (final (axis, _) in _padMoves[dir]!) {
        _mount.moveAxis(axis: axis, rate: 0).catchError((Object _) => false);
      }
      // Arms the settle timer only, so the flag can't stay on after the
      // window goes (a later GoTo must still block the pad).
      _moving.released();
    }
    _focus.dispose();
    super.dispose();
  }

  void _press(String dir) {
    if (widget.blocker != null) return;
    if (_held != null) _release();
    ref.read(simplePointingProvider.notifier).moved();
    _moving.pressed();
    setState(() => _held = dir);
    _refusalShown = false;
    _start();
    _renew = Timer.periodic(kSimplePadRenew, (_) => _start());
  }

  void _start() {
    final dir = _held;
    if (dir == null) return;
    for (final (axis, sign) in _padMoves[dir]!) {
      _send(axis, sign * _rate, lease: kSimplePadLeaseMs);
    }
    // The position follows the move: many mounts report a hand move as
    // tracking, not slewing, so the busy poll never starts.
    _mount.refresh().ignore();
  }

  void _release() {
    final dir = _held;
    _renew?.cancel();
    _renew = null;
    if (dir == null) return;
    if (mounted) setState(() => _held = null);
    for (final (axis, _) in _padMoves[dir]!) {
      _send(axis, 0);
    }
    _mount.refresh().ignore();
    _moving.released();
  }

  void _stopAll() {
    _renew?.cancel();
    _renew = null;
    // A press this ends: its key or finger may lift later and find nothing
    // held, so the flag settles here (a later GoTo must block the pad).
    if (_held != null) _moving.released();
    if (mounted) setState(() => _held = null);
    try {
      ref.read(mountProvider.notifier).abortSlew().catchError((Object e) {
        if (mounted) _refuse(e);
        return false;
      });
    } catch (_) {
      // Torn down mid-call; the lease stops a leased move on the rig.
    }
  }

  // Fire-and-forget: a release must never wait. A refused start is said once
  // per press; stop legs stay silent (the lease and Stop back them up).
  void _send(int axis, double rate, {int? lease}) {
    try {
      _mount.moveAxis(axis: axis, rate: rate, leaseMs: lease).catchError((
        Object e,
      ) {
        if (rate != 0 && mounted) _refuse(e);
        return false;
      });
    } catch (_) {
      // ref.read during teardown; the lease is the backstop.
    }
  }

  void _refuse(Object e) {
    if (_refusalShown) return;
    _refusalShown = true;
    final l10n = context.l10n;
    userNotifier(context, origin: SimpleStatusOrigin.mount)(
      l10n.deviceCouldntMoveMount,
      detail: failureReason(l10n, e),
      error: true,
    );
    // A refused start stops renewing: the rig said no.
    _renew?.cancel();
    _renew = null;
  }

  /// The arrow keys or Escape were used since the last pointer down: the ring
  /// shows, so a keyboard user sees the pad has focus (WCAG 2.4.7) and a
  /// click never draws it (user, 2026-11-11: it read as a stray contour).
  bool _keyRing = false;

  void _showKeyRing() {
    if (!_keyRing && mounted) setState(() => _keyRing = true);
  }

  KeyEventResult _key(FocusNode _, KeyEvent e) {
    final keys = {
      LogicalKeyboardKey.arrowUp: 'n',
      LogicalKeyboardKey.arrowDown: 's',
      LogicalKeyboardKey.arrowLeft: 'w',
      LogicalKeyboardKey.arrowRight: 'e',
    };
    if (e.logicalKey == LogicalKeyboardKey.escape && e is KeyDownEvent) {
      _showKeyRing();
      _stopAll();
      return KeyEventResult.handled;
    }
    // H is the centre's Home, so the keyboard reaches it too (WCAG 2.1.1).
    if (e.logicalKey == LogicalKeyboardKey.keyH) {
      if (e is KeyDownEvent && widget.canHome && !widget.moving) {
        _showKeyRing();
        widget.onHome?.call();
      }
      return KeyEventResult.handled;
    }
    final dir = keys[e.logicalKey];
    if (dir == null) return KeyEventResult.ignored;
    _showKeyRing();
    if (e is KeyDownEvent) {
      _press(dir);
    } else if (e is KeyUpEvent && _held == dir) {
      _release();
    }
    return KeyEventResult.handled;
  }

  @override
  Widget build(BuildContext context) {
    final l10n = context.l10n;
    final off = widget.blocker != null;
    final size = kSimplePadCell * simpleRailScale(context);
    Widget cell(String dir, String label, String semantics) => _PadCell(
      key: Key('simple-mount-pad-$dir'),
      size: size,
      label: label,
      semantics: semantics,
      held: _held == dir,
      small: dir.length == 2,
      onDown: off ? null : () => _press(dir),
      onUp: () {
        if (_held == dir) _release();
      },
    );
    final n = l10n.padNorth,
        s = l10n.padSouth,
        e = l10n.padEast,
        w = l10n.padWest;
    final rows = [
      [
        cell('nw', '$n$w', l10n.padMoveNorthWest),
        cell('n', n, l10n.padMoveNorth),
        cell('ne', '$n$e', l10n.padMoveNorthEast),
      ],
      [
        cell('w', w, l10n.padMoveWest),
        if (widget.canHome && !widget.moving)
          _PadCell(
            key: const Key('simple-mount-pad-home'),
            size: size,
            label: l10n.deviceHome,
            semantics: l10n.deviceHome,
            tooltip: l10n.deviceHome,
            // A slew: started on letting go, so a brush doesn't send the
            // mount home (Stop stays on touch).
            onRelease: true,
            glyph: Icon(
              Icons.home_rounded,
              size: size * 0.45,
              color: AraColors.textPrimary,
            ),
            onDown: widget.onHome,
            onUp: () {},
          )
        else
          _PadCell(
            key: const Key('simple-mount-pad-stop'),
            size: size,
            label: l10n.deviceStop,
            semantics: l10n.padStopMount,
            // The red square grows with the cell, as the house does.
            glyph: SizedBox.square(
              dimension: size * kSimpleStopSquare / kSimplePadCell,
              child: const FittedBox(child: SimpleStopSquare()),
            ),
            onDown: _stopAll,
            onUp: () {},
          ),
        cell('e', e, l10n.padMoveEast),
      ],
      [
        cell('sw', '$s$w', l10n.padMoveSouthWest),
        cell('s', s, l10n.padMoveSouth),
        cell('se', '$s$e', l10n.padMoveSouthEast),
      ],
    ];
    final width = size * 3 + AraSpace.s8 * 2;
    return Focus(
      focusNode: _focus,
      onFocusChange: (has) {
        if (!has && _keyRing) setState(() => _keyRing = false);
      },
      child: Listener(
        behavior: HitTestBehavior.translucent,
        onPointerDown: (_) {
          _focus.requestFocus();
          if (_keyRing) setState(() => _keyRing = false);
        },
        child: Container(
          key: _keyRing ? const Key('simple-mount-pad-focus') : null,
          width: width,
          // The dials' ring (2 pt Info Blue), drawn over the pad so nothing moves.
          foregroundDecoration: _keyRing
              ? BoxDecoration(
                  border: Border.all(color: AraColors.accentInfo, width: 2),
                  borderRadius: BorderRadius.circular(AraRadius.simplePanel),
                )
              : null,
          child: Column(
            mainAxisSize: MainAxisSize.min,
            spacing: AraSpace.s8,
            children: [
              if (off)
                SizedBox(
                  height: 44,
                  child: Center(
                    child: Text(
                      widget.blocker!,
                      key: const Key('simple-mount-pad-why'),
                      textAlign: TextAlign.center,
                      maxLines: 2,
                      overflow: TextOverflow.ellipsis,
                      style: kSimpleCaption,
                    ),
                  ),
                )
              else if (widget.speeds.length > 1)
                SimpleSegments<String>(
                  keyPrefix: 'simple-mount-speed',
                  group: l10n.padSpeed,
                  shrinkToFit: true,
                  options: [
                    for (final (id, _) in widget.speeds)
                      (
                        id,
                        switch (id) {
                          'slow' => l10n.padSlow,
                          'medium' => l10n.padMedium,
                          _ => l10n.padFast,
                        },
                        id,
                      ),
                  ],
                  selected: _speed,
                  onPick: (id) => setState(() => _speed = id),
                )
              else
                const SizedBox(height: 44),
              for (final row in rows)
                Row(
                  mainAxisAlignment: MainAxisAlignment.spaceBetween,
                  children: row,
                ),
            ],
          ),
        ),
      ),
    );
  }
}

class _PadCell extends StatelessWidget {
  const _PadCell({
    super.key,
    required this.label,
    required this.semantics,
    required this.onDown,
    required this.onUp,
    required this.size,
    this.held = false,
    this.small = false,
    this.glyph,
    this.tooltip,
    this.onRelease = false,
  });

  /// Named on hover and long press (Home's house).
  final String? tooltip;

  /// [onDown] runs on letting go instead, and not at all when the touch is
  /// cancelled (Home: a move must not start on a brush).
  final bool onRelease;

  final double size;
  final String label;

  /// Drawn instead of [label] (Home's house, Stop's red square); the label
  /// stays for screen readers.
  final Widget? glyph;
  final String semantics;
  final VoidCallback? onDown;
  final VoidCallback onUp;
  final bool held;
  final bool small;

  @override
  Widget build(BuildContext context) {
    final cell = _cell();
    // The label already names it for screen readers: the tooltip is for eyes.
    return tooltip == null
        ? cell
        : Tooltip(message: tooltip!, excludeFromSemantics: true, child: cell);
  }

  Widget _cell() => Semantics(
    button: true,
    enabled: onDown != null,
    label: semantics,
    excludeSemantics: true,
    // A screen reader's double tap: a short nudge, then stop.
    onTap: onDown == null
        ? null
        : () {
            onDown!();
            Future<void>.delayed(const Duration(milliseconds: 500), onUp);
          },
    child: Opacity(
      opacity: onDown == null ? 0.35 : 1,
      // A tap, for Home: it loses the touch to a slide off or a scroll, so a
      // swipe that starts on it sends nothing. Directions and Stop act on touch.
      child: onRelease
          ? GestureDetector(onTap: onDown, child: _face())
          : Listener(
              onPointerDown: onDown == null ? null : (_) => onDown!(),
              onPointerUp: (_) => onUp(),
              onPointerCancel: (_) => onUp(),
              child: _face(),
            ),
    ),
  );

  Widget _face() => Container(
    width: size,
    height: size,
    alignment: Alignment.center,
    decoration: BoxDecoration(
      color: held ? AraColors.selectionBg : AraColors.tileFill,
      borderRadius: BorderRadius.circular(AraRadius.simplePanel),
    ),
    child:
        glyph ??
        Text(
          label,
          style: kSimpleText.copyWith(
            fontSize: small
                ? AraFontSize.simpleUnit
                : AraFontSize.simplePadLetter,
            fontWeight: FontWeight.w600,
            color: held
                ? AraColors.selectionFg
                : small
                ? AraColors.textSecondary
                : AraColors.textPrimary,
          ),
        ),
  );
}

enum SimplePointingPhase { idle, solving, result, centring, error }

/// What the Pointing row shows: the last check's answer, a failure with the
/// settings panel that fixes it, or the routine under way.
class SimplePointing {
  const SimplePointing({
    this.phase = SimplePointingPhase.idle,
    this.result,
    this.error,
    this.fix,
    this.synced = false,
  });

  final SimplePointingPhase phase;

  /// The last check, kept under a centring so its figures stay.
  final VerifyPositionResult? result;
  final String? error;

  /// (settings panel id, button label) for [error].
  final (String, String)? fix;

  /// Sync went through, until the next check.
  final bool synced;

  bool get working =>
      phase == SimplePointingPhase.solving ||
      phase == SimplePointingPhase.centring;
}

/// The Mount window's Check / Centre / Sync, kept while the window is open
/// (it clears when the window closes; a check under way carries on).
class SimplePointingNotifier extends Notifier<SimplePointing> {
  Timer? _poll;
  String? _job;

  /// The mount moved while the frame was taken: its answer is already old.
  bool _movedDuringCheck = false;

  @override
  SimplePointing build() {
    ref.onDispose(() => _poll?.cancel());
    // A slew from anywhere (a GoTo, a park, a pad nudge the mount reports)
    // makes the answer old.
    ref.listen(mountProvider.select((s) => s.value?.isBusy ?? false), (
      _,
      busy,
    ) {
      if (busy) moved();
    });
    // A check or centring that ends badly with no window open to show it says
    // so in a message pill, as any failed routine does (DESIGN.md pill rule).
    listenSelf((prev, next) {
      if (_windows > 0 || prev == null || !prev.working) return;
      final l10n = _l10n;
      final title = prev.phase == SimplePointingPhase.centring
          ? l10n.pointingCouldntCentre
          : l10n.pointingCouldntSolve;
      final messages = ref.read(simpleMessagesProvider.notifier);
      if (next.phase == SimplePointingPhase.error) {
        messages.show(
          SimpleMessageKind.error,
          title,
          detail: next.error,
          origin: SimpleStatusOrigin.mount,
        );
      } else if (next.phase == SimplePointingPhase.result &&
          next.result?.success == false) {
        // Amber: the rig works, the frame didn't solve.
        messages.show(
          SimpleMessageKind.refused,
          title,
          detail: next.result?.note,
          origin: SimpleStatusOrigin.mount,
        );
      }
    });
    return const SimplePointing();
  }

  /// Mount windows open now: with none, a failure goes to a message pill.
  int _windows = 0;

  void opened() => _windows++;

  /// The Mount window closed: its answer goes with it (a check or centring
  /// under way carries on, and its pill with it).
  void closed() {
    if (_windows > 0) _windows--;
    if (ref.mounted && !state.working) state = const SimplePointing();
  }

  /// The mount moved since the check: drop the answer, so Sync can never
  /// write an old solve into the mount's model. A centring moves the mount
  /// itself and keeps going.
  void moved() {
    if (state.phase == SimplePointingPhase.solving) {
      _movedDuringCheck = true;
    } else if (!state.working && state.phase != SimplePointingPhase.idle) {
      state = const SimplePointing();
    }
  }

  AppLocalizations get _l10n => l10nFor(ref.read(appLocaleProvider));

  /// One quick frame at [exposureSeconds], solved on the rig.
  Future<void> check({double? exposureSeconds}) async {
    final api = ref.read(plateSolveApiProvider);
    if (api == null || state.working) return;
    final l10n = _l10n;
    state = SimplePointing(
      phase: SimplePointingPhase.solving,
      result: state.result,
    );
    _movedDuringCheck = false;
    try {
      final r = await api.verifyPosition(exposureSeconds: exposureSeconds);
      if (!ref.mounted) return;
      state = _movedDuringCheck
          ? const SimplePointing()
          : SimplePointing(phase: SimplePointingPhase.result, result: r);
    } on PlateSolveException catch (e) {
      if (!ref.mounted) return;
      // A 409 is the camera, and its reason says what fixes it: only a missing
      // camera is fixed in Camera settings; a busy one is waited out and a
      // stuck gate needs a restart (#1786). A 422 names the optics or the
      // solver (#1650).
      final m = e.message.toLowerCase();
      final busy = e.statusCode == 409 && e.reason == 'camera-busy';
      state = SimplePointing(
        phase: SimplePointingPhase.error,
        error: busy ? l10n.pointingCameraBusy : e.message,
        fix: e.statusCode == 409
            ? (e.reason == 'camera-busy' || e.reason == 'gate-stuck'
                  ? null
                  : ('eq.camera', l10n.pointingOpenCamera))
            : m.contains('focal') || m.contains('pixel') || m.contains('optic')
            ? ('img.optics', l10n.pointingOpenOptics)
            : ('img.platesolve', l10n.pointingOpenPlateSolving),
      );
    } catch (e) {
      if (!ref.mounted) return;
      state = SimplePointing(
        phase: SimplePointingPhase.error,
        error: failureReason(l10n, e),
      );
    }
  }

  /// Slews and centres on [raDeg] / [decDeg] (J2000) by repeated solves, then
  /// checks again.
  Future<void> centre(double raDeg, double decDeg) async {
    final api = ref.read(centerApiProvider);
    if (api == null || state.working) return;
    final l10n = _l10n;
    state = SimplePointing(
      phase: SimplePointingPhase.centring,
      result: state.result,
    );
    try {
      final job = await api.start(raDeg / 15, decDeg);
      if (!ref.mounted) return;
      _job = job.jobId;
      _poll = Timer.periodic(const Duration(seconds: 1), (_) => _follow(api));
    } catch (e) {
      if (!ref.mounted) return;
      state = SimplePointing(
        phase: SimplePointingPhase.error,
        error: '${l10n.pointingCouldntCentre}: ${failureReason(l10n, e)}',
      );
    }
  }

  Future<void> _follow(CenterClient api) async {
    final id = _job;
    if (id == null) return;
    AutofocusJob? job;
    try {
      job = await api.job(id);
    } catch (_) {
      return; // a missed poll; the next one asks again
    }
    if (!ref.mounted || job == null || !job.isTerminal || _job != id) return;
    _poll?.cancel();
    _job = null;
    switch (job.state) {
      case 'complete':
        state = SimplePointing(
          phase: SimplePointingPhase.result,
          result: state.result,
        );
        await check();
      case 'cancelled':
        state = SimplePointing(
          phase: SimplePointingPhase.result,
          result: state.result,
        );
      default:
        state = SimplePointing(
          phase: SimplePointingPhase.error,
          error: job.errorMessage ?? _l10n.pointingCouldntCentre,
        );
    }
  }

  Future<void> cancelCentre() async {
    final id = _job;
    final api = ref.read(centerApiProvider);
    if (id == null || api == null) return;
    try {
      await api.cancel(id);
    } catch (_) {
      // The next poll reads whatever the job did.
    }
  }

  /// Writes the solve into the mount's model; true when the mount took it.
  Future<bool> sync() async {
    final r = state.result;
    final ra = r?.solvedRaHours, dec = r?.solvedDecDegrees;
    if (r == null || ra == null || dec == null) return false;
    // The solve is J2000; a sync is written in the mount's own frame.
    final at = r.mountEpoch == 'JNOW'
        ? j2000ToApparent(ra * 15, dec, atUtc: clock.now().toUtc())
        : (raDeg: ra * 15, decDeg: dec);
    final ok = await ref
        .read(mountProvider.notifier)
        .slewTo(at.raDeg / 15 % 24, at.decDeg, sync: true);
    if (ok && ref.mounted) {
      state = SimplePointing(phase: state.phase, result: r, synced: true);
    }
    return ok;
  }
}

// Kept while the app runs (the status pill watches it); the window clears it
// when it closes.
final simplePointingProvider =
    NotifierProvider<SimplePointingNotifier, SimplePointing>(
      SimplePointingNotifier.new,
    );

/// The intended position of a check in J2000 (the centring routine's frame).
({double raDeg, double decDeg})? simpleIntendedJ2000(VerifyPositionResult r) {
  final ra = r.intendedRaHours, dec = r.intendedDecDegrees;
  if (ra == null || dec == null) return null;
  return r.mountEpoch == 'JNOW'
      ? apparentToJ2000(ra * 15, dec, atUtc: clock.now().toUtc())
      : (raDeg: ra * 15, decDeg: dec);
}

/// The Pointing row: the mount's RA/Dec (the solved one after a check) with
/// Check beside it, and why Check is off under it.
class SimpleMountPointingRow extends ConsumerWidget {
  const SimpleMountPointingRow({
    super.key,
    required this.mount,
    required this.busy,
  });

  final MountStatus mount;

  /// Why Check is off (the main camera busy, polar alignment), or null.
  final String? busy;

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final l10n = context.l10n;
    final p = ref.watch(simplePointingProvider);
    final r = p.phase == SimplePointingPhase.result ? p.result : null;
    final solved = r != null && r.success && r.solvedRaHours != null;
    final ra = solved ? r.solvedRaHours : mount.rightAscensionHours;
    final dec = solved ? r.solvedDecDegrees : mount.declinationDegrees;
    final exposure = ref.watch(
      exposureControllerProvider.select((e) => e.exposure),
    );
    return Column(
      crossAxisAlignment: CrossAxisAlignment.stretch,
      spacing: AraSpace.s4,
      children: [
        ConstrainedBox(
          constraints: const BoxConstraints(minHeight: 48),
          // Bottom-aligned, so Check's bottom is the row's (the pad lines up
          // with it).
          child: Row(
            crossAxisAlignment: CrossAxisAlignment.end,
            spacing: AraSpace.s8,
            children: [
              Expanded(
                child: MergeSemantics(
                  child: Column(
                    crossAxisAlignment: CrossAxisAlignment.start,
                    children: [
                      Text(
                        solved ? l10n.pointingSolved : l10n.pointingLabel,
                        style: kSimpleCaption,
                      ),
                      Text(
                        '${formatRaHours(ra)} · ${formatDecDegrees(dec)}',
                        key: const Key('simple-mount-pointing-radec'),
                        style: kSimpleText.copyWith(
                          fontFeatures: const [FontFeature.tabularFigures()],
                        ),
                      ),
                    ],
                  ),
                ),
              ),
              Tooltip(
                message: l10n.pointingCheckHint,
                child: SimpleWindowButton(
                  key: const Key('simple-mount-check'),
                  label: l10n.pointingCheck,
                  onTap: busy != null || p.working
                      ? null
                      : () => ref
                            .read(simplePointingProvider.notifier)
                            .check(
                              exposureSeconds: exposure.inMilliseconds / 1000,
                            ),
                ),
              ),
            ],
          ),
        ),
        if (busy case final why?)
          Text(
            why,
            key: const Key('simple-mount-check-why'),
            style: kSimpleCaption,
          ),
      ],
    );
  }
}

/// Under the Pointing row: a check or centring under way, a failure with the
/// setting that fixes it, or the answer: how far from the Target, with Sync
/// while nothing runs ([idle]) and Centre when more than 1′ off.
class SimpleMountPointingResult extends ConsumerWidget {
  const SimpleMountPointingResult({
    super.key,
    required this.idle,
    required this.busy,
  });

  /// Nothing runs (no plan imaging, no guiding, the mount still and
  /// unparked): Sync may write into the mount's model.
  final bool idle;

  /// Why Check (and so Centre) is off, or null.
  final String? busy;

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final l10n = context.l10n;
    final p = ref.watch(simplePointingProvider);
    final notifier = ref.read(simplePointingProvider.notifier);
    final working = kSimpleText.copyWith(color: AraColors.accentConnected);
    final children = <Widget>[
      if (p.phase == SimplePointingPhase.solving)
        StatusBlink(
          on: true,
          child: Text(l10n.pointingSolving, style: working),
        ),
      if (p.phase == SimplePointingPhase.centring)
        Row(
          spacing: AraSpace.s8,
          children: [
            Expanded(
              child: StatusBlink(
                on: true,
                child: Text(l10n.pointingCentring, style: working),
              ),
            ),
            SimpleWindowButton(
              key: const Key('simple-mount-centre-cancel'),
              label: l10n.deviceCancel,
              onTap: notifier.cancelCentre,
            ),
          ],
        ),
      if (p.phase == SimplePointingPhase.error) ...[
        Text(
          p.error ?? '',
          key: const Key('simple-mount-pointing-error'),
          style: kSimpleText.copyWith(color: AraColors.errorText),
        ),
        if (p.fix case (final panel, final label))
          Align(
            alignment: Alignment.centerLeft,
            child: TextButton(
              key: const Key('simple-mount-pointing-fix'),
              onPressed: () => openSettingsPanel(ref, panel),
              child: Text(label),
            ),
          ),
      ],
      if (p.phase == SimplePointingPhase.result && p.result != null)
        ..._answer(context, ref, l10n, p),
    ];
    // Announced as it changes, as the pills are; not the progress words, which
    // the status pill already announces.
    return Semantics(
      liveRegion: !p.working,
      container: true,
      child: Column(
        key: const Key('simple-mount-pointing'),
        crossAxisAlignment: CrossAxisAlignment.stretch,
        spacing: AraSpace.s4,
        children: children,
      ),
    );
  }

  List<Widget> _answer(
    BuildContext context,
    WidgetRef ref,
    AppLocalizations l10n,
    SimplePointing p,
  ) {
    final r = p.result!;
    if (!r.success) {
      return [
        Text(
          l10n.pointingCouldntSolve,
          key: const Key('simple-mount-pointing-unsolved'),
          // Amber: the rig works, this frame didn't solve (user, 2026-11-11).
          style: kSimpleText.copyWith(color: AraColors.accentWarning),
        ),
        if (r.note case final note?) Text(note, style: kSimpleCaption),
      ];
    }
    final target = simpleIntendedJ2000(r);
    final off = r.solvedToIntendedArcmin;
    final name = target == null
        ? null
        : simplePointingTargetName(
            ref.watch(simpleRunProvider.select((s) => s.targets)),
            target.raDeg,
            target.decDeg,
          );
    final captions = [
      if (r.solvedToMountArcmin case final m?)
        l10n.pointingMountOff(simpleSeparation(m)),
      if (r.rotatorSyncedDegrees case final a?)
        l10n.pointingRotatorSynced('${a.toStringAsFixed(1)}°'),
    ];
    Future<void> sync() async {
      final notify = userNotifier(context, origin: SimpleStatusOrigin.mount);
      try {
        if (await ref.read(simplePointingProvider.notifier).sync()) {
          notify(l10n.pointingSyncedPill);
        }
      } catch (e) {
        notify(
          l10n.pointingCouldntSync,
          detail: failureReason(l10n, e),
          error: true,
        );
      }
    }

    return [
      Row(
        spacing: AraSpace.s8,
        children: [
          Expanded(
            child: off == null
                ? const SizedBox.shrink()
                : Text(
                    name == null
                        ? l10n.pointingFromLastGoto(simpleSeparation(off))
                        : l10n.pointingFrom(simpleSeparation(off), name),
                    key: const Key('simple-mount-pointing-offset'),
                    style: kSimpleText,
                  ),
          ),
          if (off != null && off <= 1)
            Text(
              l10n.pointingOnTarget,
              style: kSimpleText.copyWith(color: AraColors.accentConnected),
            ),
          if (idle)
            SimpleWindowButton(
              key: const Key('simple-mount-sync'),
              label: p.synced ? l10n.pointingSynced : l10n.pointingSync,
              onTap: p.synced ? null : sync,
            ),
          if (off != null && off > 1 && target != null)
            SimpleWindowButton(
              key: const Key('simple-mount-centre'),
              label: l10n.pointingCentre,
              onTap: busy != null
                  ? null
                  : () => ref
                        .read(simplePointingProvider.notifier)
                        .centre(target.raDeg, target.decDeg),
            ),
        ],
      ),
      if (captions.isNotEmpty)
        Text(
          captions.join(' · '),
          key: const Key('simple-mount-pointing-captions'),
          style: kSimpleCaption,
        ),
    ];
  }
}
