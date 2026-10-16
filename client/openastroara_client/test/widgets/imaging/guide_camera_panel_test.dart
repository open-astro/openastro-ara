import 'dart:convert';
import 'dart:typed_data';

import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/models/guider_calibration_view.dart';
import 'package:openastroara/models/guider_live.dart';
import 'package:openastroara/models/server.dart';
import 'package:openastroara/services/guider_live_api.dart';
import 'package:openastroara/state/guider/guider_live_state.dart';
import 'package:openastroara/state/saved_server_state.dart';
import 'package:openastroara/widgets/imaging/guide_calibration_panel.dart';
import 'package:openastroara/widgets/imaging/guide_camera_panel.dart';

/// Serves one live view; records the actions.
class _FakeLive implements GuiderLiveClient {
  GuiderLive view;
  final calls = <String>[];
  _FakeLive(this.view);
  @override
  Future<GuiderLive> getLive() async => view;
  // A real 1×1 PNG: Image.memory decodes it, so the overlay and tap layers mount.
  static final Uint8List _png = base64Decode(
    'iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNkYAAAAAYAAjCB0C8AAAAASUVORK5CYII=',
  );
  @override
  Future<Uint8List?> getFrame() async => view.hasFrame ? _png : null;
  @override
  Future<void> loop() async => calls.add('loop');
  @override
  Future<void> stop() async => calls.add('stop');
  @override
  Future<void> selectStar(double x, double y) async =>
      calls.add('select $x $y');
  @override
  Future<void> deselectStar() async => calls.add('deselect');
  GuiderCalibrationView calibration = const GuiderCalibrationView(
    connected: true,
    verdict: 'redo',
    verdictReason: 'No calibration stored — the next guide start calibrates.',
  );
  bool calibrationFails = false;
  @override
  Future<GuiderCalibrationView> getCalibration() async {
    if (calibrationFails) throw StateError('calibration read failed');
    return calibration;
  }

  @override
  Future<void> clearCalibration() async => calls.add('clear_calibration');
  @override
  void close() {}
}

GuiderLive _looping() => GuiderLive.fromJson({
  'connected': true,
  'app_state': 'Looping',
  'frame_width': 1280,
  'frame_height': 960,
  'lock_x': 640.0,
  'lock_y': 480.0,
  'search_region': 15,
  'snr': 42.0,
  'star_mass': 12300.0,
  'multi_star_enabled': true,
  'stars': [
    {'x': 640.0, 'y': 480.0, 'snr': 42.0},
    {'x': 100.0, 'y': 200.0, 'snr': 12.0},
  ],
  'has_frame': true,
});

void main() {
  test('the live view parses and summarises', () {
    final v = _looping();
    expect(v.connected, isTrue);
    expect(v.isLooping, isTrue);
    expect(v.hasLock, isTrue);
    expect(v.stars, hasLength(2));
    expect(
      v.summary,
      'Looping',
      reason: 'the last guide frame\'s SNR is not shown while looping',
    );
    expect(
      GuiderLive.fromJson({
        'connected': true,
        'app_state': 'Guiding',
        'snr': 42.0,
        'star_mass': 12300.0,
      }).summary,
      'Guiding · SNR 42 · mass 12.3k',
    );
    expect(
      GuiderLive.fromJson({'connected': false}).summary,
      'Guider not connected',
    );
    expect(
      GuiderLive.fromJson({'connected': true, 'app_state': 'LostLock'}).summary,
      'Star lost',
    );
  });

  testWidgets('a paused guide offers Stop, not Loop', (t) async {
    final fake = _FakeLive(
      GuiderLive.fromJson({'connected': true, 'app_state': 'Paused'}),
    );
    await t.pumpWidget(
      ProviderScope(
        overrides: [
          activeServerProvider.overrideWithValue(
            const AraServer(hostname: 'localhost', port: 5555),
          ),
          guiderLiveApiFactoryProvider.overrideWithValue((_) => fake),
        ],
        child: const MaterialApp(
          home: Scaffold(
            body: SizedBox(width: 1000, height: 600, child: GuideCameraPanel()),
          ),
        ),
      ),
    );
    await t.pump();
    await t.pump();
    expect(
      t
          .widget<FilledButton>(find.widgetWithText(FilledButton, 'Loop'))
          .onPressed,
      isNull,
      reason: 'Loop would throw the paused guide away',
    );
    expect(
      t
          .widget<FilledButton>(find.widgetWithText(FilledButton, 'Stop'))
          .onPressed,
      isNotNull,
    );
  });

  test('frame pixels map onto a BoxFit.contain picture', () {
    // 1280×960 in a 1000×500 box: scale 0.5208, picture 666.7×500, 166.7 px letterbox each side.
    const box = Size(1000, 500);
    final centre = GuideMarksPainter.toCanvas(640, 480, 1280, 960, box);
    expect(centre.dx, closeTo(500, 0.01));
    expect(centre.dy, closeTo(250, 0.01));
    final origin = GuideMarksPainter.toCanvas(0, 0, 1280, 960, box);
    expect(origin.dx, closeTo(166.67, 0.01));
    expect(origin.dy, closeTo(0, 0.01));
    expect(
      GuideMarksPainter.scaleFor(1280, 960, box),
      closeTo(500 / 960, 1e-9),
    );
  });

  testWidgets(
    'the panel shows the guider state and sends a tap as a star pick',
    (t) async {
      final fake = _FakeLive(_looping());
      await t.pumpWidget(
        ProviderScope(
          overrides: [
            activeServerProvider.overrideWithValue(
              const AraServer(hostname: 'localhost', port: 5555),
            ),
            guiderLiveApiFactoryProvider.overrideWithValue((_) => fake),
          ],
          child: const MaterialApp(
            home: Scaffold(
              body: SizedBox(
                width: 1000,
                height: 600,
                child: GuideCameraPanel(),
              ),
            ),
          ),
        ),
      );
      await t.pump();
      await t.pump();
      expect(find.text('Looping'), findsOneWidget);
      expect(find.text('Multi-star'), findsOneWidget);
      expect(find.byKey(const Key('guide-camera-hint')), findsOneWidget);
      // Tap the picture's centre: the frame's middle in guide pixels.
      await t.tap(find.byType(GuideCameraView));
      await t.pump();
      await t.pump();
      expect(
        fake.calls.where((c) => c.startsWith('select')),
        hasLength(1),
        reason: fake.calls.join(','),
      );
      final pick = fake.calls
          .firstWhere((c) => c.startsWith('select'))
          .split(' ');
      expect(double.parse(pick[1]), closeTo(640, 3));
      expect(double.parse(pick[2]), closeTo(480, 3));
      // Stop is the live action while looping; Loop is not.
      expect(
        t
            .widget<FilledButton>(find.widgetWithText(FilledButton, 'Loop'))
            .onPressed,
        isNull,
      );
      await t.tap(find.widgetWithText(FilledButton, 'Stop'));
      await t.pump();
      expect(fake.calls, contains('stop'));
    },
  );

  test(
    'the calibration view parses and the plot scale fits the farthest step',
    () {
      final v = GuiderCalibrationView.fromJson({
        'connected': true,
        'run_state': 'complete',
        'steps': [
          {'direction': 'West', 'step': 1, 'dx': -1.3, 'dy': -0.1, 'dist': 1.3},
          {'direction': 'West', 'step': 2, 'dx': -2.5, 'dy': -0.6, 'dist': 2.6},
          {'direction': 'North', 'step': 1, 'dx': -0.6, 'dy': 2.0, 'dist': 2.1},
          {
            'direction': 'North',
            'step': 8,
            'dx': -5.3,
            'dy': 13.4,
            'dist': 14.4,
          },
        ],
        'data': {
          'calibrated': true,
          'x_angle_deg': 172.4,
          'y_angle_deg': 84.1,
          'x_rate_px_per_sec': 12.1,
          'y_rate_px_per_sec': 11.6,
          'declination_deg': 44.2,
          'orthogonality_error_deg': 1.7,
        },
        'verdict': 'good',
      });
      expect(v.steps, hasLength(4));
      expect(v.steps.first.isRa, isTrue);
      expect(v.steps.last.isRa, isFalse);
      expect(v.data!.orthogonalityErrorDeg, 1.7);
      expect(v.verdict, 'good');
      expect(GuideCalibrationPanel.verdictStyle(v).$1, 'Good');
      expect(
        GuideCalibrationPanel.verdictStyle(
          const GuiderCalibrationView(connected: true, runState: 'running'),
        ).$1,
        'Calibrating…',
      );
      expect(
        GuideCalibrationPanel.verdictStyle(GuiderCalibrationView.disconnected)
            .$1,
        'No guider',
      );
      // 13.4 px is the farthest step: it lands 12% inside a 200-px square's half-side.
      expect(
        CalibrationStepsPainter.scaleFor(v.steps, 200) * 13.4,
        closeTo(88, 1e-9),
      );
    },
  );

  testWidgets('the panel shows the calibration verdict and clears on request', (
    t,
  ) async {
    final fake = _FakeLive(_looping());
    fake.calibration = const GuiderCalibrationView(
      connected: true,
      runState: 'complete',
      verdict: 'check',
      verdictReason: 'RA and Dec axes are 15° from square — the mount or camera may have moved during calibration.',
    );
    await t.pumpWidget(
      ProviderScope(
        overrides: [
          activeServerProvider.overrideWithValue(
            const AraServer(hostname: 'localhost', port: 5555),
          ),
          guiderLiveApiFactoryProvider.overrideWithValue((_) => fake),
        ],
        child: const MaterialApp(
          home: Scaffold(
            body: SizedBox(width: 1000, height: 600, child: GuideCameraPanel()),
          ),
        ),
      ),
    );
    await t.pump();
    await t.pump();
    expect(find.byKey(const Key('calibration-verdict')), findsOneWidget);
    expect(find.text('Check'), findsOneWidget);
    expect(find.textContaining('15° from square'), findsOneWidget);
    await t.tap(find.text('Clear calibration'));
    await t.pump();
    expect(fake.calls, contains('clear_calibration'));
  });

  testWidgets('a failed calibration read keeps the picture and the state', (
    t,
  ) async {
    final fake = _FakeLive(_looping())..calibrationFails = true;
    await t.pumpWidget(
      ProviderScope(
        overrides: [
          activeServerProvider.overrideWithValue(
            const AraServer(hostname: 'localhost', port: 5555),
          ),
          guiderLiveApiFactoryProvider.overrideWithValue((_) => fake),
        ],
        child: const MaterialApp(
          home: Scaffold(
            body: SizedBox(width: 1000, height: 600, child: GuideCameraPanel()),
          ),
        ),
      ),
    );
    await t.pump();
    await t.pump();
    expect(
      find.text('Looping · SNR 42 · mass 12.3k'),
      findsNothing,
      reason: 'SNR only while guiding',
    );
    expect(find.byKey(const Key('guide-camera-summary')), findsOneWidget);
    expect(
      find.textContaining('Looping'),
      findsWidgets,
      reason: 'the live state still landed',
    );
    expect(
      find.textContaining('calibration read failed'),
      findsNothing,
      reason: 'no error for the side column',
    );
  });
}
