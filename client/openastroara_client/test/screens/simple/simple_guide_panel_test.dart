import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/models/guider_status.dart';
import 'package:openastroara/screens/simple/simple_guide_panel.dart';
import 'package:openastroara/services/guider_api.dart' show GuiderClient;
import 'package:openastroara/state/guider/guider_state.dart';
import 'package:openastroara/state/settings/phd2_settings_state.dart';
import 'package:openastroara/widgets/equipment/guider_chip.dart' show guiderStatusLevel;
import 'package:openastroara/widgets/status_indicator.dart' show StatusLevel;

class _Guider extends GuiderStatusNotifier {
  _Guider(this.json, {this.lag = false});
  final Map<String, dynamic> json;
  final calls = <String>[];

  /// The rig acts a moment after the ask: the state doesn't change yet.
  final bool lag;

  // No server here: a read keeps what the rig last said.
  @override
  Future<void> refresh([GuiderClient? client]) async {}
  @override
  Future<GuiderStatus?> build() async => GuiderStatus.fromJson(json);
  // As the rig: the routine starts (or stops) and the next read shows it.
  @override
  Future<void> startGuiding() async {
    calls.add('start');
    if (lag) return;
    state = AsyncData(GuiderStatus.fromJson({...json, 'runtime': <String, dynamic>{...json['runtime'] as Map<String, dynamic>, 'state': 'looping'}}));
  }
  @override
  Future<void> stopGuiding() async {
    calls.add('stop');
    state = AsyncData(GuiderStatus.fromJson({...json, 'runtime': <String, dynamic>{...json['runtime'] as Map<String, dynamic>, 'state': 'stopped'}}));
  }
}

Map<String, dynamic> _wire({String state = 'connected', String runtime = 'guiding'}) => {
      'device_id': 'g',
      'name': 'PHD2',
      'state': state,
      'runtime': {
        'state': runtime,
        'rms_total': 0.9,
        'rms_total_arcsec': 0.62,
        'rms_ra_arcsec': 0.41,
        'rms_dec_arcsec': 0.47,
      },
    };

late _Guider _g;

Future<ProviderContainer> _pump(WidgetTester tester, Map<String, dynamic> wire,
    {Widget child = const SimpleGuidePanel(), bool lag = false}) async {
  _g = _Guider(wire, lag: lag);
  final c = ProviderContainer(overrides: [guiderStatusProvider.overrideWith(() => _g)]);
  addTearDown(c.dispose);
  await tester.pumpWidget(UncontrolledProviderScope(
    container: c,
    child: MaterialApp(home: Scaffold(body: Center(child: child))),
  ));
  await tester.pump();
  return c;
}

void main() {
  group('what the guider is doing, for the shutter and Guided Run', () {
    GuiderStatus at(String state, String runtime) =>
        GuiderStatus.fromJson({'device_id': 'g', 'name': 'PHD2', 'state': state, 'runtime': {'state': runtime}});
    test('active: anything the red square stops', () {
      for (final r in ['looping', 'calibrating', 'guiding', 'settling', 'paused', 'star_lost']) {
        expect(simpleGuideActive(at('connected', r)), isTrue, reason: r);
      }
      expect(simpleGuideActive(at('connected', 'stopped')), isFalse);
      expect(simpleGuideActive(at('disconnected', 'guiding')), isFalse);
      expect(simpleGuideActive(null), isFalse);
    });
    test('guiding: guiding, settling or dithering, not calibrating', () {
      expect(simpleGuiding(at('connected', 'guiding')), isTrue);
      expect(simpleGuiding(at('connected', 'calibrating')), isFalse);
      expect(simpleGuiding(at('connected', 'stopped')), isFalse);
    });
  });

  testWidgets('the Guide panel: what it is doing and its RMS, in arcseconds', (tester) async {
    await _pump(tester, _wire());
    expect(find.text('Guiding'), findsOneWidget);
    expect(find.text('0.62″'), findsOneWidget);
    expect(find.text('RA 0.41″ · Dec 0.47″'), findsOneWidget);
  });

  testWidgets('idle: Not guiding, not Stopped, and how to start', (tester) async {
    await _pump(tester, _wire(runtime: 'stopped'));
    expect(find.text('Not guiding'), findsOneWidget);
    expect(find.text('Stopped'), findsNothing);
    expect(find.textContaining('Tap the shutter to start guiding'), findsOneWidget);
    expect(find.text('—'), findsOneWidget, reason: 'no RMS while idle');
  });

  testWidgets('no guider: says to connect it', (tester) async {
    await _pump(tester, _wire(state: 'disconnected', runtime: 'stopped'));
    expect(find.text('Guider not connected'), findsOneWidget);
  });

  testWidgets("Guide's shutter starts the guider's routine; while it runs, the square stops it", (tester) async {
    await _pump(tester, _wire(runtime: 'stopped'), child: const SimpleGuideShutter());
    expect(find.bySemanticsLabel('Start guiding'), findsOneWidget);
    await tester.tap(find.byType(SimpleGuideShutter));
    await tester.pump();
    expect(_g.calls, ['start']);
    await _pump(tester, _wire(runtime: 'calibrating'), child: const SimpleGuideShutter());
    expect(find.bySemanticsLabel('Stop guiding'), findsOneWidget);
    await tester.tap(find.byType(SimpleGuideShutter));
    await tester.pump();
    expect(_g.calls, ['stop']);
  });

  testWidgets('a lost star is red in the panel, as in the top bar and the pill (iPad, 2026-11-04: it was amber)',
      (tester) async {
    await _pump(tester, _wire(runtime: 'star_lost'));
    final dot = find.descendant(of: find.byKey(const Key('simple-guide-panel')), matching: find.byType(Container)).evaluate()
        .map((e) => (e.widget as Container).decoration)
        .whereType<BoxDecoration>()
        .where((d) => d.shape == BoxShape.circle)
        .single;
    expect(dot.color, guiderStatusLevel(GuiderStatus.fromJson(_wire(runtime: 'star_lost'))).color);
    expect(dot.color, StatusLevel.error.color);
  });

  testWidgets('a tap says Starting… at once, before the guider answers (iPad, 2026-11-04: feedback was slow)',
      (tester) async {
    await _pump(tester, _wire(runtime: 'stopped'), lag: true,
        child: const Column(mainAxisSize: MainAxisSize.min, children: [SimpleGuidePanel(), SimpleGuideShutter()]));
    await tester.tap(find.byType(SimpleGuideShutter));
    await tester.pump();
    expect(find.text('Starting…'), findsOneWidget);
    expect(find.bySemanticsLabel('Stop guiding'), findsNothing, reason: 'no second tap while the ask is out');
    await tester.pump(const Duration(seconds: 11)); // the guider never answered: back to what it says
    expect(find.text('Starting…'), findsNothing);
    expect(find.text('Not guiding'), findsOneWidget);
  });

  testWidgets("the guide camera's exposure, a tile like Preview's, and its gain slider (user, 2026-11-04)",
      (tester) async {
    final c = await _pump(tester, _wire());
    c.read(phd2SettingsProvider.notifier).setGuideExposureMs(2000);
    await tester.pump();
    final tile = find.byKey(const Key('simple-guide-exposure'));
    expect(find.descendant(of: tile, matching: find.text('2 s')), findsOneWidget);
    final slider = tester.widget<Slider>(find.byKey(const Key('simple-guider-gain')));
    expect((slider.min, slider.max), (0.0, 100.0), reason: "the Guider Camera window's slider, one value");
    slider.onChangeEnd!(70);
    await tester.pump();
    expect(c.read(phd2SettingsProvider).guideCameraGain, 70);
  });

  testWidgets('Dither every: frames up and down, Off below 1, kept in the profile', (tester) async {
    final c = await _pump(tester, _wire());
    c.read(phd2SettingsProvider.notifier)
      ..setDitherEnabled(true)
      ..setDitherEveryNFrames(2);
    await tester.pump();
    expect(find.text('2 frames'), findsOneWidget);
    await tester.tap(find.byKey(const Key('simple-guider-dither-up')));
    await tester.pump();
    expect(c.read(phd2SettingsProvider).ditherEveryNFrames, 3);
    for (var i = 0; i < 3; i++) {
      await tester.tap(find.byKey(const Key('simple-guider-dither-down')));
      await tester.pump();
    }
    expect(c.read(phd2SettingsProvider).ditherEnabled, isFalse);
    expect(find.text('Off'), findsOneWidget);
    await tester.tap(find.byKey(const Key('simple-guider-dither-up')));
    await tester.pump();
    expect((c.read(phd2SettingsProvider).ditherEnabled, c.read(phd2SettingsProvider).ditherEveryNFrames), (true, 1));
    expect(find.text('Every frame'), findsOneWidget);
  });

  testWidgets('dither distance in pixels, only while dithering', (tester) async {
    final c = await _pump(tester, _wire());
    c.read(phd2SettingsProvider.notifier)
      ..setDitherEnabled(true)
      ..setDitherPixels(5);
    await tester.pump();
    expect(find.text('5 px'), findsOneWidget);
    await tester.tap(find.byKey(const Key('simple-guider-dither-px-up')));
    await tester.pump();
    expect(c.read(phd2SettingsProvider).ditherPixels, 6);
    c.read(phd2SettingsProvider.notifier).setDitherEnabled(false);
    await tester.pump();
    expect(find.byKey(const Key('simple-guider-dither-px-up')), findsNothing);
  });
}
