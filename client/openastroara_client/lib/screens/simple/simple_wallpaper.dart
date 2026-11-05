import 'dart:async';
import 'dart:io' show Platform;
import 'dart:math' as math;

import 'package:flutter/foundation.dart' show kIsWeb;
import 'package:flutter/material.dart';
import 'package:sensors_plus/sensors_plus.dart';

import '../../theme/ara_colors.dart';

/// The accelerometer's reading in g, on the device's axes: x to its right
/// edge, y to its top, z out of the screen (at rest it points up).
typedef Gravity = ({double x, double y, double z});

/// Simple Mode's wallpaper while there is no frame (#1602): the brand navy and
/// three layers of stars, which shift by depth as an iPad or iPhone tilts.
/// Still with Reduce Motion (iOS) or Remove animations (Android), and on
/// desktop.
class SimpleWallpaper extends StatefulWidget {
  const SimpleWallpaper({super.key, this.gravity});

  /// The accelerometer on iOS and Android unless given (tests).
  final Stream<Gravity>? gravity;

  @override
  State<SimpleWallpaper> createState() => _SimpleWallpaperState();
}

class _SimpleWallpaperState extends State<SimpleWallpaper>
    with WidgetsBindingObserver {
  /// Each layer's shift in px at full tilt: far stars, mid, near.
  static const _depths = [6.0, 14.0, 26.0];

  /// About 20° of tilt is full tilt.
  static const _fullTilt = .35;

  /// A change smaller than this (about 1 px on the nearest layer) is ignored,
  /// so a rig on a stand doesn't redraw for sensor noise.
  static const _deadBand = .03;

  StreamSubscription<Gravity>? _sub;

  /// Which device axis points to the screen's bottom (a unit vector in the
  /// device's x/y plane), from the last reading held up off the table.
  Offset? _down;

  /// The pose that counts as centre, as (roll, pitch) in radians; it drifts
  /// toward the current pose so a rig picked up and set down recentres.
  Offset? _centre;
  Offset _shift = Offset.zero;

  bool get _still {
    final a11y = View.of(context).platformDispatcher.accessibilityFeatures;
    return a11y.reduceMotion || a11y.disableAnimations;
  }

  @override
  void initState() {
    super.initState();
    WidgetsBinding.instance.addObserver(this);
  }

  @override
  void didChangeDependencies() {
    super.didChangeDependencies();
    _listen();
  }

  @override
  void didChangeAccessibilityFeatures() => setState(_listen);

  /// Reads the sensor only while it can move something.
  void _listen() {
    if (_still) {
      _sub?.cancel();
      _sub = null;
      _shift = Offset.zero;
      _centre = null;
      return;
    }
    if (_sub != null) return;
    // dart:io's platform is the host's, so tests on a desktop never listen.
    final gravity =
        widget.gravity ??
        (!kIsWeb && (Platform.isIOS || Platform.isAndroid)
            ? accelerometerEventStream(
                samplingPeriod: SensorInterval.uiInterval,
              ).map((e) => (x: e.x / 9.81, y: e.y / 9.81, z: e.z / 9.81))
            : null);
    _sub = gravity?.listen(
      _onGravity,
      onError: (_) {
        _sub?.cancel();
        _sub = null;
      },
    );
  }

  void _onGravity(Gravity g) {
    final flat = math.sqrt(g.x * g.x + g.y * g.y);
    // Held up, the screen's bottom is the device axis gravity pulls along
    // most; flat on a table, keep the last one (the screen keeps its
    // rotation too). Before any, guess from the window's shape.
    if (flat > .5) {
      _down = g.x.abs() > g.y.abs()
          ? Offset(-g.x.sign, 0)
          : Offset(0, -g.y.sign);
    }
    final down =
        _down ??
        (MediaQuery.orientationOf(context) == Orientation.portrait
            ? const Offset(0, -1)
            : const Offset(1, 0));
    // The screen's axes on the device: right is down turned a quarter.
    final right = Offset(-down.dy, down.dx);
    final up = -(g.x * down.dx + g.y * down.dy);
    final side = g.x * right.dx + g.y * right.dy;
    // Angles, not raw gravity, so a tilt reads the same upright or flat.
    final pose = Offset(
      math.atan2(side, math.sqrt(up * up + g.z * g.z)),
      math.atan2(up, g.z),
    );
    final centre = _centre = _centre == null
        ? pose
        : Offset.lerp(_centre, pose, .005)!;
    final d = (pose - centre) / _fullTilt;
    final shift = Offset(d.dx.clamp(-1.0, 1.0), d.dy.clamp(-1.0, 1.0));
    if ((shift - _shift).distance < _deadBand) return;
    setState(() => _shift = shift);
  }

  @override
  void dispose() {
    WidgetsBinding.instance.removeObserver(this);
    _sub?.cancel();
    super.dispose();
  }

  @override
  Widget build(BuildContext context) {
    // The animation eases between readings, so sensor jitter doesn't shimmer.
    return TweenAnimationBuilder<Offset>(
      tween: Tween(end: _shift),
      duration: const Duration(milliseconds: 150),
      // Built once: a shift moves the cached layers, it doesn't repaint stars.
      child: const _Layers(),
      builder: (context, shift, layers) => DecoratedBox(
        decoration: const BoxDecoration(
          gradient: LinearGradient(
            begin: Alignment.topCenter,
            end: Alignment.bottomCenter,
            colors: [
              AraColors.brandNavyTop,
              AraColors.brandNavyMid,
              AraColors.brandNavyBottom,
            ],
            stops: [0, .45, 1],
          ),
        ),
        child: _Shift(shift: shift, child: layers!),
      ),
    );
  }
}

/// Passes the current shift down to the layers without rebuilding them.
class _Shift extends InheritedWidget {
  const _Shift({required this.shift, required super.child});

  final Offset shift;

  @override
  bool updateShouldNotify(_Shift old) => old.shift != shift;
}

class _Layers extends StatelessWidget {
  const _Layers();

  @override
  Widget build(BuildContext context) => Stack(
    clipBehavior: Clip.hardEdge,
    children: [
      for (final (i, depth) in _SimpleWallpaperState._depths.indexed)
        Positioned(
          // Overhangs by its depth so a shift never shows an edge.
          left: -depth,
          top: -depth,
          right: -depth,
          bottom: -depth,
          child: _Layer(index: i, depth: depth),
        ),
    ],
  );
}

class _Layer extends StatelessWidget {
  const _Layer({required this.index, required this.depth});

  final int index;
  final double depth;

  @override
  Widget build(BuildContext context) => Transform.translate(
    key: Key('simple-wallpaper-layer-$index'),
    offset:
        -context.dependOnInheritedWidgetOfExactType<_Shift>()!.shift * depth,
    child: RepaintBoundary(child: CustomPaint(painter: _Stars(index))),
  );
}

/// One layer of stars, the same every time: far ones many and faint, near
/// ones few and bright.
class _Stars extends CustomPainter {
  const _Stars(this.layer);

  final int layer;

  @override
  void paint(Canvas canvas, Size size) {
    final r = math.Random(7 + layer);
    final paint = Paint();
    for (var n = 0; n < const [260, 90, 28][layer]; n++) {
      paint.color = Colors.white.withValues(
        alpha: const [.35, .6, .9][layer] * (.6 + r.nextDouble() * .4),
      );
      canvas.drawCircle(
        Offset(r.nextDouble() * size.width, r.nextDouble() * size.height),
        const [.5, .9, 1.6][layer] * (.7 + r.nextDouble() * .6),
        paint,
      );
    }
  }

  @override
  bool shouldRepaint(_Stars old) => old.layer != layer;
}
