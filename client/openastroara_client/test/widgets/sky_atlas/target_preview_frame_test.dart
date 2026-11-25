import 'dart:math' as math;

import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/services/tonight_sky_api.dart';
import 'package:openastroara/state/sky_atlas/target_preview_state.dart';
import 'package:openastroara/widgets/sky_atlas/target_preview.dart';

/// #2150: the frame box on the survey preview turns the way the camera does.
/// The dial is the camera's up axis in degrees east of north (the daemon's
/// meaning); the survey cutout has north up and east on the left, so a
/// positive dial turns the box counter-clockwise on screen.

final _m31 = TonightSkyObject(
  id: 'M31',
  name: 'Andromeda Galaxy',
  type: 'galaxy',
  magnitude: 3.4,
  raDeg: 10.6847,
  decDeg: 41.269,
  altitudeDeg: 55,
  maxAltitudeDeg: 60,
  windowStartUtc: DateTime.utc(2026, 6, 29, 20, 14),
  windowEndUtc: DateTime.utc(2026, 6, 30, 4, 32),
  transitUtc: DateTime.utc(2026, 6, 30, 1, 10),
  integrationHours: 6.3,
  remainingHours: 3.2,
  framing: TonightFraming.good,
  score: 88,
  scoreReasons: const [],
);

void main() {
  testWidgets('the preview box turns counter-clockwise for a positive dial',
      (tester) async {
    await tester.pumpWidget(ProviderScope(
      overrides: [
        targetPreviewProvider.overrideWith((ref, key) async => null),
      ],
      child: MaterialApp(
        home: Scaffold(
          body: SizedBox(
            width: 320,
            child: TargetPreview(
              object: _m31,
              frameFovArcmin: (187.0, 125.0),
              rotationDeg: 30,
            ),
          ),
        ),
      ),
    ));
    await tester.pump();
    final frame = find.descendant(
      of: find.byType(TargetPreview),
      matching: find.byWidgetPredicate((w) =>
          w is CustomPaint &&
          w.painter.runtimeType.toString() == '_FramePainter'),
    );
    expect(frame, findsOneWidget);
    // Flutter's canvas.rotate is clockwise for a positive angle (y down).
    expect(frame, paints..rotate(angle: -30 * math.pi / 180));
  });
}
