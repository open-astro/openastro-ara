import 'dart:typed_data';

import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/l10n/l10n.dart';
import 'package:openastroara/models/polar_align.dart';
import 'package:openastroara/services/polar_align_api.dart';
import 'package:openastroara/state/polar_align/polar_align_state.dart';
import 'package:openastroara/state/polar_align/polar_corrector_state.dart';
import 'package:openastroara/state/settings/site_settings_state.dart';
import 'package:openastroara/theme/ara_colors.dart';
import 'package:openastroara/widgets/imaging/polar_align_panel.dart';
import 'package:openastroara/widgets/imaging/polar_corrector_block.dart';
import 'package:openastroara/widgets/status_indicator.dart';

class _Api implements PolarAlignClient {
  PolarAlignSettings settings = const PolarAlignSettings(targetToleranceArcmin: 2);
  @override
  Future<PolarAlignStatus?> getStatus() async => null;
  @override
  Future<PolarAlignSettings> getSettings() async => settings;
  @override
  Future<PolarAlignSettings> putSettings(PolarAlignSettings next) async => settings = next;
  @override
  Future<void> start({String? stage}) async {}
  @override
  Future<void> next() async {}
  @override
  Future<void> stop() async {}
  @override
  Future<void> complete() async {}
  @override
  Future<void> requestCapture() async {}
  @override
  Future<Uint8List?> getLiveFrame() async => null;
  @override
  void close() {}
}

class _Corrector implements PolarCorrectorClient {
  _Corrector(this.device);
  PolarCorrector? device;
  final calls = <String>[];
  @override
  Future<PolarCorrector?> get() async {
    calls.add('get');
    return device;
  }

  @override
  Future<void> stop() async => calls.add('stop');
  @override
  Future<void> home() async => calls.add('home');
  @override
  Future<void> stopInRoutine() async => calls.add('stopInRoutine');
  @override
  Future<void> resumeInRoutine() async => calls.add('resumeInRoutine');
  @override
  void close() {}
}

class _Live extends PolarAlignLiveNotifier {
  _Live(this.live);
  final PolarAlignLive live;
  @override
  PolarAlignLive build() => live;
}

class _Site extends SiteSettingsNotifier {
  _Site(this.lat);
  final double lat;
  @override
  SiteSettings build() => SiteSettings(latitudeDeg: lat);
}

const _base = PolarCorrector(
  present: true,
  carrier: 'switch',
  name: 'Polar base',
  drivable: true,
  azimuthPositionArcmin: 12.4,
  altitudePositionArcmin: -3,
);

PolarAlignLive _run(PolarCorrectorRun run) =>
    PolarAlignLive(phase: PolarAlignStates.adjusting, totalErrorArcmin: 8, corrector: run);

Future<_Corrector> _pump(WidgetTester tester,
    {PolarCorrector? device = _base, PolarAlignLive live = const PolarAlignLive(), bool auto = true, double lat = 40}) async {
  final corrector = _Corrector(device);
  // A fresh scope each time: overrideWith keeps the first notifier otherwise.
  await tester.pumpWidget(const SizedBox());
  await tester.pumpWidget(ProviderScope(
    overrides: [
      polarAlignApiProvider.overrideWithValue(
          _Api()..settings = PolarAlignSettings(targetToleranceArcmin: 2, polarCorrectorAuto: auto)),
      polarCorrectorApiProvider.overrideWithValue(corrector),
      polarAlignLiveProvider.overrideWith(() => _Live(live)),
      siteSettingsProvider.overrideWith(() => _Site(lat)),
    ],
    child: MaterialApp(
      // Steady dots, so the blink lets pumpAndSettle settle.
      builder: (c, w) => MediaQuery(data: MediaQuery.of(c).copyWith(disableAnimations: true), child: w!),
      home: const Scaffold(body: SingleChildScrollView(child: SizedBox(width: 420, child: PolarCorrectorBlock()))),
    ),
  ));
  await tester.pumpAndSettle();
  return corrector;
}

Finder _key(String k) => find.byKey(Key(k));

void main() {
  final en = lookupAppLocalizations(const Locale('en'));
  final pt = lookupAppLocalizations(const Locale('pt', 'BR'));

  test('polarCorrectorDirection: + azimuth is east in the north and west in the south; + altitude is up', () {
    expect(polarCorrectorDirection(en, 'azimuth', 2, southern: false), 'east');
    expect(polarCorrectorDirection(en, 'azimuth', -2, southern: false), 'west');
    expect(polarCorrectorDirection(en, 'azimuth', 2, southern: true), 'west');
    expect(polarCorrectorDirection(en, 'azimuth', -2, southern: true), 'east');
    expect(polarCorrectorDirection(en, 'altitude', 1, southern: false), 'up');
    expect(polarCorrectorDirection(en, 'altitude', -1, southern: true), 'down');
    expect(polarCorrectorDirection(pt, 'azimuth', 2, southern: false), 'para leste');
  });

  testWidgets('no Polar Corrector: the block draws nothing', (tester) async {
    await _pump(tester, device: const PolarCorrector());
    expect(_key('polar-corrector-block'), findsNothing);
    await _pump(tester, device: null);
    expect(_key('polar-corrector-block'), findsNothing);
  });

  testWidgets('idle: the start sentence, the position from its zero and Home', (tester) async {
    final c = await _pump(tester);
    expect(find.text('Polar Corrector'), findsOneWidget);
    expect(find.text('Polar base'), findsOneWidget);
    expect(find.textContaining('Tap the shutter to start.'), findsOneWidget);
    expect(find.textContaining('inside your 2′ tolerance'), findsOneWidget);
    expect(find.text('From its zero: azimuth +12.4′ · altitude −3.0′'), findsOneWidget);
    expect(_key('polar-corrector-stop'), findsNothing);
    await tester.tap(_key('polar-corrector-home'));
    await tester.pumpAndSettle();
    expect(c.calls, contains('home'));
    expect(tester.getSize(_key('polar-corrector-home')).height, greaterThanOrEqualTo(44));
  });

  testWidgets('position not reported: a sentence, never 0; one axis missing shows a dash', (tester) async {
    await _pump(tester, device: const PolarCorrector(present: true, name: 'Polar base', drivable: true));
    expect(find.text("This device doesn't report its position."), findsOneWidget);
    await _pump(tester,
        device: const PolarCorrector(present: true, drivable: true, azimuthPositionArcmin: 1.0));
    expect(find.text('From its zero: azimuth +1.0′ · altitude —'), findsOneWidget);
  });

  testWidgets('by hand: Turned by off says Motors off, with no buttons', (tester) async {
    await _pump(tester, auto: false, live: _run(const PolarCorrectorRun(state: 'probing', axis: 'azimuth', arcmin: 2)));
    expect(find.text('Motors off. Turn the bolts by hand.'), findsOneWidget);
    expect(find.byType(ButtonStyleButton), findsNothing);
  });

  testWidgets('manual run state says Motors off too', (tester) async {
    await _pump(tester, live: _run(const PolarCorrectorRun(state: 'manual')));
    expect(find.text('Motors off. Turn the bolts by hand.'), findsOneWidget);
  });

  testWidgets('probing: the test move with axis, amount and direction; Stop motors stops them in the routine',
      (tester) async {
    final c = await _pump(tester, live: _run(const PolarCorrectorRun(state: 'probing', axis: 'azimuth', arcmin: 2)));
    expect(find.text('Test move · azimuth 2.0′ east'), findsOneWidget);
    expect(_key('polar-corrector-home'), findsNothing, reason: 'never while the motors run');
    final stop = tester.widget<FilledButton>(_key('polar-corrector-stop'));
    expect(stop.style!.backgroundColor!.resolve({}), AraColors.emergencyStop);
    await tester.tap(_key('polar-corrector-stop'));
    await tester.pumpAndSettle();
    expect(c.calls, contains('stopInRoutine'));
  });

  testWidgets('moving: the direction word follows the sign and the site hemisphere', (tester) async {
    await _pump(tester, live: _run(const PolarCorrectorRun(state: 'moving', axis: 'azimuth', arcmin: 6.1)));
    expect(find.text('Moving azimuth 6.1′ east'), findsOneWidget);
    await _pump(tester, lat: -35, live: _run(const PolarCorrectorRun(state: 'moving', axis: 'azimuth', arcmin: 6.1)));
    expect(find.text('Moving azimuth 6.1′ west'), findsOneWidget);
    await _pump(tester, live: _run(const PolarCorrectorRun(state: 'moving', axis: 'azimuth', arcmin: -6.1)));
    expect(find.text('Moving azimuth 6.1′ west'), findsOneWidget);
    await _pump(tester, live: _run(const PolarCorrectorRun(state: 'moving', axis: 'altitude', arcmin: 1.5)));
    expect(find.text('Moving altitude 1.5′ up'), findsOneWidget);
    await _pump(tester, lat: -35, live: _run(const PolarCorrectorRun(state: 'moving', axis: 'altitude', arcmin: -1.5)));
    expect(find.text('Moving altitude 1.5′ down'), findsOneWidget);
    expect(_key('polar-corrector-stop'), findsOneWidget);
  });

  testWidgets('settling: the settle seconds beside a ring, with Stop motors', (tester) async {
    await _pump(tester, live: _run(const PolarCorrectorRun(state: 'settling', seconds: 3)));
    expect(find.text('Letting the mount settle · 3 s'), findsOneWidget);
    expect(tester.widget<StatusDot>(find.byType(StatusDot)).ring, isTrue);
    expect(_key('polar-corrector-stop'), findsOneWidget);
  });

  testWidgets('measuring: the measuring line with Stop motors', (tester) async {
    await _pump(tester, live: _run(const PolarCorrectorRun(state: 'measuring', moves: 2)));
    expect(find.text('Measuring the new error…'), findsOneWidget);
    expect(_key('polar-corrector-stop'), findsOneWidget);
  });

  testWidgets('converged: the tolerance and the moves, no buttons', (tester) async {
    await _pump(tester, live: _run(const PolarCorrectorRun(state: 'converged', moves: 4)));
    expect(
        find.text('Inside your 2′ tolerance after 4 moves. Finish with Verify or Done. '
            'If the error drifts out, the motors correct again.'),
        findsOneWidget);
    expect(find.byType(ButtonStyleButton), findsNothing);
  });

  const causes = {
    'wrong_direction': ('azimuth', null,
        'The test move on azimuth went the wrong way, so Ara stopped the motors. The Polar Corrector may be set up reversed on that axis.'),
    'wrong_scale': ('altitude', null,
        "The test move on altitude didn't go the distance asked, so Ara stopped the motors. Check the Polar Corrector's step setting."),
    'device_fault': ('azimuth', 'Azimuth: end of travel', 'The Polar Corrector reported a fault: “Azimuth: end of travel”.'),
    'not_calibrated': (null, 'Not calibrated',
        "The Polar Corrector isn't calibrated, so it refused to move: “Not calibrated”. Calibrate it in its own app, then Resume."),
    'device_refused': (null, 'Altitude: move exceeds limit', 'The Polar Corrector refused the move: “Altitude: move exceeds limit”.'),
    'diverging': (null, null, 'The error grew after two corrections in a row, so Ara stopped the motors.'),
    'timeout': (null, null, 'The Polar Corrector was still moving after 60 s, so Ara stopped it.'),
    'link_lost': (null, null, 'Lost the link to the Polar Corrector. Ara stops its motors as soon as it answers again.'),
    'mount_tracking': (null, null,
        "The Polar Corrector can't move while the mount tracks. Turn tracking off in the Mount window, then Resume."),
    'mount_slewing': (null, null, "The Polar Corrector can't move while the mount slews. Resume once the mount stops."),
    'stopped_by_user': (null, null, 'You stopped the motors.'),
  };

  test('every stop cause has its own sentence in English and pt-BR', () {
    expect(causes.keys.toSet(), PolarCorrectorCauses.all.toSet());
    final unknownEn = en.polarCorrectorStopUnknown, unknownPt = pt.polarCorrectorStopUnknown;
    for (final MapEntry(key: cause, value: (axis, text, _)) in causes.entries) {
      final run = PolarCorrectorRun(state: 'stopped', cause: cause, axis: axis, deviceText: text);
      expect(polarCorrectorStopText(en, run), isNot(unknownEn), reason: cause);
      expect(polarCorrectorStopText(pt, run), isNot(unknownPt), reason: cause);
      expect(polarCorrectorStopText(pt, run), isNot(polarCorrectorStopText(en, run)), reason: cause);
    }
  });

  test('an unknown cause says Ara stopped the motors, with the device text when there is one', () {
    expect(polarCorrectorStopText(en, const PolarCorrectorRun(state: 'stopped', cause: 'new_cause')),
        'Ara stopped the motors.');
    expect(
        polarCorrectorStopText(en, const PolarCorrectorRun(state: 'stopped', cause: 'new_cause', deviceText: 'Overheated')),
        'Ara stopped the motors: “Overheated”.');
  });

  for (final MapEntry(key: cause, value: (axis, text, sentence)) in causes.entries) {
    testWidgets('stopped ($cause): its sentence, carry on by hand, Home and Resume', (tester) async {
      final c = await _pump(tester,
          live: _run(PolarCorrectorRun(state: 'stopped', cause: cause, axis: axis, deviceText: text, moves: 2)));
      expect(find.text(sentence), findsOneWidget);
      expect(find.text('Carry on by hand: the arrows show which bolt to turn.'), findsOneWidget);
      expect(_key('polar-corrector-stop'), findsNothing);
      final lost = cause == 'link_lost';
      final resume = tester.widget<FilledButton>(_key('polar-corrector-resume'));
      final home = tester.widget<OutlinedButton>(_key('polar-corrector-home'));
      expect(resume.onPressed == null, lost);
      expect(home.onPressed == null, lost);
      expect(find.text('Resume and Home come back once it answers.'), lost ? findsOneWidget : findsNothing);
      expect(_key('polar-corrector-position'), lost ? findsNothing : findsOneWidget);
      if (!lost) {
        await tester.tap(_key('polar-corrector-resume'));
        await tester.pumpAndSettle();
        expect(c.calls, contains('resumeInRoutine'));
      }
    });
  }

  testWidgets('not drivable (runs its own loop): the mock\'s two sentences, nothing to press', (tester) async {
    await _pump(tester,
        device: const PolarCorrector(present: true, name: 'The mount', carrier: 'mount', notDrivableReason: 'device_runs_its_own_loop'));
    expect(
        find.text("Ara can't move this Polar Corrector yet: it only runs its own alignment. Turn the bolts by hand."),
        findsOneWidget);
    expect(find.byType(ButtonStyleButton), findsNothing);
  });

  testWidgets('not drivable (info unreadable): the mock\'s two sentences', (tester) async {
    await _pump(tester, device: const PolarCorrector(present: true, notDrivableReason: 'info_unreadable'));
    expect(
        find.text("Ara couldn't read what this Polar Corrector can do, so it won't move it. Turn the bolts by hand."),
        findsOneWidget);
  });

  testWidgets('homing: the device moving outside a correction says so, and Stop stops the device', (tester) async {
    final c = await _pump(tester, device: const PolarCorrector(present: true, drivable: true, moving: true));
    expect(find.text('Going back to its zero…'), findsOneWidget);
    expect(_key('polar-corrector-home'), findsNothing);
    await tester.tap(_key('polar-corrector-stop'));
    await tester.pumpAndSettle();
    expect(c.calls, contains('stop'));
  });

  testWidgets('a phone width at 2x text: the stopped block does not overflow', (tester) async {
    tester.view.physicalSize = const Size(390, 844);
    tester.view.devicePixelRatio = 1;
    addTearDown(tester.view.reset);
    final corrector = _Corrector(_base);
    await tester.pumpWidget(ProviderScope(
      overrides: [
        polarAlignApiProvider.overrideWithValue(_Api()),
        polarCorrectorApiProvider.overrideWithValue(corrector),
        polarAlignLiveProvider.overrideWith(() => _Live(_run(const PolarCorrectorRun(
            state: 'stopped', cause: 'not_calibrated', deviceText: 'Not calibrated')))),
      ],
      child: MaterialApp(
        builder: (c, w) => MediaQuery(
            data: MediaQuery.of(c).copyWith(disableAnimations: true, textScaler: const TextScaler.linear(2)), child: w!),
        home: const Scaffold(body: SingleChildScrollView(child: SizedBox(width: 280, child: PolarCorrectorBlock()))),
      ),
    ));
    await tester.pumpAndSettle();
    expect(tester.takeException(), isNull);
    expect(_key('polar-corrector-resume'), findsOneWidget);
  });

  testWidgets('the status card line: the move while the motors work, Stopped in amber after', (tester) async {
    expect(polarCorrectorStatusLine(en, const PolarAlignLive(), southern: false), isNull);
    final moving = polarCorrectorStatusLine(en,
        _run(const PolarCorrectorRun(state: 'moving', axis: 'azimuth', arcmin: 6.1)), southern: false)!;
    expect((moving.text, moving.color, moving.working), ('Moving azimuth 6.1′ east', AraColors.accentConnected, true));
    final settling = polarCorrectorStatusLine(en, _run(const PolarCorrectorRun(state: 'settling', seconds: 2)), southern: false)!;
    expect((settling.ring, settling.color), (true, AraColors.accentInfo));
    final stopped = polarCorrectorStatusLine(en, _run(const PolarCorrectorRun(state: 'stopped', cause: 'diverging')), southern: false)!;
    expect((stopped.text, stopped.color, stopped.working), ('Stopped', AraColors.accentBusy, false));
    expect(polarCorrectorStatusLine(en, _run(const PolarCorrectorRun(state: 'converged')), southern: false), isNull);
    expect(polarCorrectorStatusLine(en, _run(const PolarCorrectorRun(state: 'manual')), southern: false), isNull);
  });

  testWidgets('Advanced Mode places the same module in its Polar Align panel, and nothing without one', (tester) async {
    Future<void> pump(PolarCorrector device) async {
      await tester.pumpWidget(const SizedBox());
      await tester.pumpWidget(ProviderScope(
        overrides: [
          polarAlignApiProvider.overrideWithValue(_Api()),
          polarCorrectorApiProvider.overrideWithValue(_Corrector(device)),
          polarAlignLiveProvider.overrideWith(() => _Live(const PolarAlignLive())),
        ],
        child: const MaterialApp(home: Scaffold(body: SingleChildScrollView(child: PolarAlignPanel()))),
      ));
      await tester.pumpAndSettle();
    }

    await pump(_base);
    expect(find.descendant(of: find.byType(PolarAlignPanel), matching: _key('polar-corrector-block')), findsOneWidget);
    expect(_key('polar-corrector-home'), findsOneWidget);
    await pump(const PolarCorrector());
    expect(_key('polar-corrector-block'), findsNothing);
  });
}
