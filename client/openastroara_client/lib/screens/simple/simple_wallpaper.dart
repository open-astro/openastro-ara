import 'dart:async';
import 'dart:io' show Platform;
import 'dart:math' as math;

import 'package:flutter/foundation.dart' show kIsWeb;
import 'package:flutter/material.dart';
import 'package:sensors_plus/sensors_plus.dart';

import '../../widgets/ara_mark.dart';

/// Simple Mode's wallpaper while there is no frame (#1602): the app icon's
/// navy, three layers of stars and the constellation, which shift by depth as
/// an iPad or iPhone tilts. Still with Reduce Motion and on desktop.
class SimpleWallpaper extends StatefulWidget {
  const SimpleWallpaper({super.key, this.tilt});

  /// The device's tilt, each axis about -1..1 (1 = lying on its side); the
  /// accelerometer on iOS and Android unless given (tests).
  final Stream<Offset>? tilt;

  @override
  State<SimpleWallpaper> createState() => _SimpleWallpaperState();
}

class _SimpleWallpaperState extends State<SimpleWallpaper> {
  /// Each layer's shift in px at full tilt: far stars, mid, near, the mark.
  static const _depths = [6.0, 14.0, 26.0, 40.0];

  StreamSubscription<Offset>? _sub;
  Offset? _centre;
  Offset _shift = Offset.zero;

  @override
  void initState() {
    super.initState();
    // dart:io's platform is the host's, so tests on a desktop never listen.
    final tilt =
        widget.tilt ??
        (!kIsWeb && (Platform.isIOS || Platform.isAndroid)
            ? accelerometerEventStream(
                samplingPeriod: SensorInterval.uiInterval,
              ).map((e) => Offset(e.x, e.y) / 9.81)
            : null);
    // shortcut: the accelerometer's axes are the device's, so in landscape a
    // tilt shifts along the other axis; map by the interface orientation if
    // that feels wrong on the iPad.
    _sub = tilt?.listen(_onTilt, onError: (_) => _sub?.cancel());
  }

  void _onTilt(Offset t) {
    // The pose it starts in is centre; ±0.35 (about 20°) is full tilt.
    final d = (t - (_centre ??= t)) / .35;
    setState(
      () => _shift = Offset(d.dx.clamp(-1.0, 1.0), d.dy.clamp(-1.0, 1.0)),
    );
  }

  @override
  void dispose() {
    _sub?.cancel();
    super.dispose();
  }

  @override
  Widget build(BuildContext context) {
    final still = MediaQuery.disableAnimationsOf(context);
    // The animation eases between readings, so sensor jitter doesn't shimmer.
    return TweenAnimationBuilder<Offset>(
      tween: Tween(end: still ? Offset.zero : _shift),
      duration: const Duration(milliseconds: 150),
      builder: (context, shift, _) => DecoratedBox(
        decoration: const BoxDecoration(
          gradient: LinearGradient(
            begin: Alignment.topCenter,
            end: Alignment.bottomCenter,
            colors: [Color(0xFF272C4D), Color(0xFF171B3B), Color(0xFF070914)],
            stops: [0, .45, 1],
          ),
        ),
        child: LayoutBuilder(
          builder: (context, box) {
            final side = math.min(box.maxWidth, box.maxHeight) * .75;
            return Stack(
              clipBehavior: Clip.hardEdge,
              children: [
                for (final (i, depth) in _depths.take(3).indexed)
                  Positioned(
                    // Overhangs by its depth so a shift never shows an edge.
                    left: -depth,
                    top: -depth,
                    right: -depth,
                    bottom: -depth,
                    child: Transform.translate(
                      key: Key('simple-wallpaper-layer-$i'),
                      offset: -shift * depth,
                      child: CustomPaint(painter: _Stars(i)),
                    ),
                  ),
                // The constellation, large and off-centre, nearest of all.
                Positioned(
                  right: side * -.05,
                  bottom: side * -.1,
                  child: Transform.translate(
                    key: const Key('simple-wallpaper-layer-3'),
                    offset: -shift * _depths[3],
                    child: AraMark(size: side),
                  ),
                ),
              ],
            );
          },
        ),
      ),
    );
  }
}

/// One layer of stars, the same every time: far ones many and faint, near
/// ones few and bright.
class _Stars extends CustomPainter {
  const _Stars(this.layer);

  final int layer;

  @override
  void paint(Canvas canvas, Size size) {
    final r = math.Random(7 + layer);
    final paint = Paint()..color = Colors.white;
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
