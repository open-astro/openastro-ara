import 'dart:typed_data';

import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/l10n/l10n.dart';
import 'package:openastroara/models/polar_align.dart';
import 'package:openastroara/services/polar_align_api.dart';
import 'package:openastroara/state/polar_align/polar_align_state.dart';
import 'package:openastroara/state/polar_align/polar_aligner_state.dart';
import 'package:openastroara/state/settings/loaded_section.dart';
import 'package:openastroara/state/settings/site_settings_state.dart';
import 'package:openastroara/theme/ara_colors.dart';
import 'package:openastroara/widgets/imaging/polar_align_panel.dart';
import 'package:openastroara/widgets/imaging/polar_aligner_block.dart';
import 'package:openastroara/widgets/status_indicator.dart';

import '../support/polar_align_section.dart';

class _Api implements PolarAlignClient {
  @override
  Future<PolarAlignStartPlan> startPlan({String? stage}) async =>
      PolarAlignStartPlan(stage: stage, direction: PolarAlignDirections.east);
  @override
  Future<PolarAlignStatus?> getStatus() async => null;
  @override
  Future<void> start(PolarAlignStartPlan plan) async {}
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

class _Aligner implements PolarAlignerClient {
  _Aligner(this.device);
  PolarAligner? device;
  final calls = <String>[];
  @override
  Future<PolarAligner?> get() async {
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
  LoadedSection<SiteSettings> build() =>
      LoadedSection(value: SiteSettings(latitudeDeg: lat), source: SectionSource.rig);
}

const _base = PolarAligner(
  present: true,
  carrier: 'switch',
  name: 'Polar base',
  drivable: true,
  azimuthPositionArcmin: 12.4,
  altitudePositionArcmin: -3,
);

PolarAlignLive _run(PolarAlignerRun run) =>
    PolarAlignLive(phase: PolarAlignStates.adjusting, totalErrorArcmin: 8, correction: run);

Future<_Aligner> _pump(WidgetTester tester,
    {PolarAligner? device = _base,
    PolarAlignLive live = const PolarAlignLive(),
    bool auto = true,
    double lat = 40,
    bool loaded = true}) async {
  final aligner = _Aligner(device);
  // A fresh scope each time: overrideWith keeps the first notifier otherwise.
  await tester.pumpWidget(const SizedBox());
  await tester.pumpWidget(ProviderScope(
    overrides: [
      polarAlignApiProvider.overrideWithValue(_Api()),
      // The profile's polar-align section: the rig's copy, or a rig that does not answer.
      ...polarAlignSectionOverrides(FakePolarAlignSection(
          initial: loaded ? rigPolarAlign.copyWith(targetToleranceArcmin: 2, polarAlignerAuto: auto) : null,
          loaded: loaded)),
      polarAlignerApiProvider.overrideWithValue(aligner),
      polarAlignLiveProvider.overrideWith(() => _Live(live)),
      siteSettingsProvider.overrideWith(() => _Site(lat)),
    ],
    child: MaterialApp(
      // Steady dots, so the blink lets pumpAndSettle settle.
      builder: (c, w) => MediaQuery(data: MediaQuery.of(c).copyWith(disableAnimations: true), child: w!),
      home: const Scaffold(body: SingleChildScrollView(child: SizedBox(width: 420, child: PolarAlignerBlock()))),
    ),
  ));
  await tester.pumpAndSettle();
  return aligner;
}

Finder _key(String k) => find.byKey(Key(k));

void main() {
  final en = lookupAppLocalizations(const Locale('en'));
  final pt = lookupAppLocalizations(const Locale('pt', 'BR'));

  test('polarAlignerDirection: + azimuth is east in the north and west in the south; + altitude is up', () {
    expect(polarAlignerDirection(en, 'azimuth', 2, southern: false), 'east');
    expect(polarAlignerDirection(en, 'azimuth', -2, southern: false), 'west');
    expect(polarAlignerDirection(en, 'azimuth', 2, southern: true), 'west');
    expect(polarAlignerDirection(en, 'azimuth', -2, southern: true), 'east');
    expect(polarAlignerDirection(en, 'altitude', 1, southern: false), 'up');
    expect(polarAlignerDirection(en, 'altitude', -1, southern: true), 'down');
    expect(polarAlignerDirection(pt, 'azimuth', 2, southern: false), 'para leste');
  });

  testWidgets('no Polar Aligner: the block draws nothing', (tester) async {
    await _pump(tester, device: const PolarAligner());
    expect(_key('polar-aligner-block'), findsNothing);
    await _pump(tester, device: null);
    expect(_key('polar-aligner-block'), findsNothing);
  });

  testWidgets('idle: the start sentence, the position from its zero and Home', (tester) async {
    final c = await _pump(tester);
    expect(find.text('Polar Aligner'), findsOneWidget);
    expect(find.text('Polar base'), findsOneWidget);
    expect(find.textContaining('Tap the shutter to start.'), findsOneWidget);
    expect(find.textContaining('inside your 2′ tolerance'), findsOneWidget);
    expect(find.text('From its zero: azimuth +12.4′ · altitude −3.0′'), findsOneWidget);
    expect(_key('polar-aligner-stop'), findsNothing);
    await tester.tap(_key('polar-aligner-home'));
    await tester.pumpAndSettle();
    expect(c.calls, contains('home'));
    expect(tester.getSize(_key('polar-aligner-home')).height, greaterThanOrEqualTo(44));
  });

  testWidgets('position not reported: a sentence, never 0; one axis missing shows a dash', (tester) async {
    await _pump(tester, device: const PolarAligner(present: true, name: 'Polar base', drivable: true));
    expect(find.text("This device doesn't report its position."), findsOneWidget);
    await _pump(tester,
        device: const PolarAligner(present: true, drivable: true, azimuthPositionArcmin: 1.0));
    expect(find.text('From its zero: azimuth +1.0′ · altitude —'), findsOneWidget);
  });

  testWidgets('by hand: Turned by off says Motors off, with no buttons', (tester) async {
    await _pump(tester, auto: false, live: _run(const PolarAlignerRun(state: 'probing', axis: 'azimuth', arcmin: 2)));
    expect(find.text('Motors off. Turn the bolts by hand.'), findsOneWidget);
    expect(find.byType(ButtonStyleButton), findsNothing);
  });

  testWidgets('manual run state says Motors off too', (tester) async {
    await _pump(tester, live: _run(const PolarAlignerRun(state: 'manual')));
    expect(find.text('Motors off. Turn the bolts by hand.'), findsOneWidget);
  });

  testWidgets('probing: the test move with axis, amount and direction; Stop motors stops them in the routine',
      (tester) async {
    final c = await _pump(tester, live: _run(const PolarAlignerRun(state: 'probing', axis: 'azimuth', arcmin: 2)));
    expect(find.text('Test move · azimuth 2.0′ east'), findsOneWidget);
    expect(_key('polar-aligner-home'), findsNothing, reason: 'never while the motors run');
    final stop = tester.widget<FilledButton>(_key('polar-aligner-stop'));
    expect(stop.style!.backgroundColor!.resolve({}), AraColors.emergencyStop);
    await tester.tap(_key('polar-aligner-stop'));
    await tester.pumpAndSettle();
    expect(c.calls, contains('stopInRoutine'));
  });

  testWidgets('moving: the direction word follows the sign and the site hemisphere', (tester) async {
    await _pump(tester, live: _run(const PolarAlignerRun(state: 'moving', axis: 'azimuth', arcmin: 6.1)));
    expect(find.text('Moving azimuth 6.1′ east'), findsOneWidget);
    await _pump(tester, lat: -35, live: _run(const PolarAlignerRun(state: 'moving', axis: 'azimuth', arcmin: 6.1)));
    expect(find.text('Moving azimuth 6.1′ west'), findsOneWidget);
    await _pump(tester, live: _run(const PolarAlignerRun(state: 'moving', axis: 'azimuth', arcmin: -6.1)));
    expect(find.text('Moving azimuth 6.1′ west'), findsOneWidget);
    await _pump(tester, live: _run(const PolarAlignerRun(state: 'moving', axis: 'altitude', arcmin: 1.5)));
    expect(find.text('Moving altitude 1.5′ up'), findsOneWidget);
    await _pump(tester, lat: -35, live: _run(const PolarAlignerRun(state: 'moving', axis: 'altitude', arcmin: -1.5)));
    expect(find.text('Moving altitude 1.5′ down'), findsOneWidget);
    expect(_key('polar-aligner-stop'), findsOneWidget);
  });

  testWidgets('settling: the settle seconds beside a ring, with Stop motors', (tester) async {
    await _pump(tester, live: _run(const PolarAlignerRun(state: 'settling', seconds: 3)));
    expect(find.text('Letting the mount settle · 3 s'), findsOneWidget);
    expect(tester.widget<StatusDot>(find.byType(StatusDot)).ring, isTrue);
    expect(_key('polar-aligner-stop'), findsOneWidget);
  });

  testWidgets('measuring: the measuring line with Stop motors', (tester) async {
    await _pump(tester, live: _run(const PolarAlignerRun(state: 'measuring', moves: 2)));
    expect(find.text('Measuring the new error…'), findsOneWidget);
    expect(_key('polar-aligner-stop'), findsOneWidget);
  });

  testWidgets('converged: the tolerance and the moves, no buttons', (tester) async {
    await _pump(tester, live: _run(const PolarAlignerRun(state: 'converged', moves: 4)));
    expect(
        find.text('Inside your 2′ tolerance after 4 moves. Finish with Check or Done. '
            'If the error drifts out, the motors correct again.'),
        findsOneWidget);
    expect(find.byType(ButtonStyleButton), findsNothing);
  });

  testWidgets(
      'the profile not loaded from the rig: no made-up tolerance, and a correction the motors make still reads as theirs (ADR 0015)',
      (tester) async {
    await _pump(tester, loaded: false, live: _run(const PolarAlignerRun(state: 'converged', moves: 4)));
    expect(
        find.text('Inside your tolerance after 4 moves. Check and Done wait for your polar-align settings to load. '
            'If the error drifts out, the motors correct again.'),
        findsOneWidget);
    expect(find.text('Motors off. Turn the bolts by hand.'), findsNothing);

    await _pump(tester, loaded: false);
    expect(find.textContaining('tolerance'), findsNothing, reason: 'idle: no tolerance until the rig answers');
    expect(_key('polar-aligner-home'), findsOneWidget);
  });

  const causes = {
    'wrong_direction': ('azimuth', null,
        'The test move on azimuth went the wrong way, so Ara stopped the motors. The Polar Aligner may be set up reversed on that axis.'),
    'wrong_scale': ('altitude', null,
        "The test move on altitude didn't go the distance asked, so Ara stopped the motors. Check the Polar Aligner's step setting."),
    'device_fault': ('azimuth', 'Azimuth: end of travel', 'The Polar Aligner reported a fault: “Azimuth: end of travel”.'),
    'not_calibrated': (null, 'Not calibrated',
        "The Polar Aligner isn't calibrated, so it refused to move: “Not calibrated”. Calibrate it in its own app, then Resume."),
    'device_refused': (null, 'Altitude: move exceeds limit', 'The Polar Aligner refused the move: “Altitude: move exceeds limit”.'),
    'diverging': (null, null, 'The error grew after two corrections in a row, so Ara stopped the motors.'),
    'timeout': (null, null, 'The Polar Aligner was still moving after 60 s, so Ara stopped it.'),
    'link_lost': (null, null, 'Lost the link to the Polar Aligner. Ara stops its motors as soon as it answers again.'),
    'mount_tracking': (null, null,
        "The Polar Aligner can't move while the mount tracks. Turn tracking off in the Mount window, then Resume."),
    'mount_slewing': (null, null, "The Polar Aligner can't move while the mount slews. Resume once the mount stops."),
    'stopped_by_user': (null, null, 'You stopped the motors.'),
  };

  test('every stop cause has its own sentence in English and pt-BR', () {
    expect(causes.keys.toSet(), PolarAlignerCauses.all.toSet());
    final unknownEn = en.polarAlignerStopUnknown, unknownPt = pt.polarAlignerStopUnknown;
    for (final MapEntry(key: cause, value: (axis, text, _)) in causes.entries) {
      final run = PolarAlignerRun(state: 'stopped', cause: cause, axis: axis, deviceText: text);
      expect(polarAlignerStopText(en, run), isNot(unknownEn), reason: cause);
      expect(polarAlignerStopText(pt, run), isNot(unknownPt), reason: cause);
      expect(polarAlignerStopText(pt, run), isNot(polarAlignerStopText(en, run)), reason: cause);
    }
  });

  test('an unknown cause says Ara stopped the motors, with the device text when there is one', () {
    expect(polarAlignerStopText(en, const PolarAlignerRun(state: 'stopped', cause: 'new_cause')),
        'Ara stopped the motors.');
    expect(
        polarAlignerStopText(en, const PolarAlignerRun(state: 'stopped', cause: 'new_cause', deviceText: 'Overheated')),
        'Ara stopped the motors: “Overheated”.');
  });

  for (final MapEntry(key: cause, value: (axis, text, sentence)) in causes.entries) {
    testWidgets('stopped ($cause): its sentence, carry on by hand, Home and Resume', (tester) async {
      final c = await _pump(tester,
          live: _run(PolarAlignerRun(state: 'stopped', cause: cause, axis: axis, deviceText: text, moves: 2)));
      expect(find.text(sentence), findsOneWidget);
      expect(find.text('Carry on by hand: the arrows show which bolt to turn.'), findsOneWidget);
      expect(_key('polar-aligner-stop'), findsNothing);
      final lost = cause == 'link_lost';
      final resume = tester.widget<FilledButton>(_key('polar-aligner-resume'));
      final home = tester.widget<OutlinedButton>(_key('polar-aligner-home'));
      // Resume is always there: after a lost link only the daemon can tell whether the device answers again.
      expect(resume.onPressed, isNotNull);
      expect(home.onPressed == null, lost);
      expect(find.text('Resume once it answers again. Home comes back then.'), lost ? findsOneWidget : findsNothing);
      expect(_key('polar-aligner-position'), lost ? findsNothing : findsOneWidget);
      await tester.tap(_key('polar-aligner-resume'));
      await tester.pumpAndSettle();
      expect(c.calls, contains('resumeInRoutine'));
    });
  }

  testWidgets('stopped: a screen reader is told why the motors stopped', (tester) async {
    final handle = tester.ensureSemantics();
    await _pump(tester, live: _run(const PolarAlignerRun(state: 'stopped', cause: 'diverging', moves: 4)));
    final node = tester.getSemantics(_key('polar-aligner-cause'));
    expect(node.flagsCollection.isLiveRegion, isTrue);
    handle.dispose();
  });

  testWidgets('not drivable (runs its own loop): the mock\'s two sentences, nothing to press', (tester) async {
    await _pump(tester,
        device: const PolarAligner(present: true, name: 'The mount', carrier: 'mount', notDrivableReason: 'device_runs_its_own_loop'));
    expect(
        find.text("Ara can't move this Polar Aligner yet: it only runs its own alignment. Turn the bolts by hand."),
        findsOneWidget);
    expect(find.byType(ButtonStyleButton), findsNothing);
  });

  testWidgets('not drivable (info unreadable): the mock\'s two sentences', (tester) async {
    await _pump(tester, device: const PolarAligner(present: true, notDrivableReason: 'info_unreadable'));
    expect(
        find.text("Ara couldn't read what this Polar Aligner can do, so it won't move it. Turn the bolts by hand."),
        findsOneWidget);
  });

  testWidgets('homing: the device moving outside a correction says so, and Stop stops the device', (tester) async {
    final c = await _pump(tester, device: const PolarAligner(present: true, drivable: true, moving: true));
    expect(find.text('Going back to its zero…'), findsOneWidget);
    expect(_key('polar-aligner-home'), findsNothing);
    await tester.tap(_key('polar-aligner-stop'));
    await tester.pumpAndSettle();
    expect(c.calls, contains('stop'));
  });

  testWidgets('a phone width at 2x text: the stopped block does not overflow', (tester) async {
    tester.view.physicalSize = const Size(390, 844);
    tester.view.devicePixelRatio = 1;
    addTearDown(tester.view.reset);
    final aligner = _Aligner(_base);
    await tester.pumpWidget(ProviderScope(
      overrides: [
        polarAlignApiProvider.overrideWithValue(_Api()),
        polarAlignerApiProvider.overrideWithValue(aligner),
        polarAlignLiveProvider.overrideWith(() => _Live(_run(const PolarAlignerRun(
            state: 'stopped', cause: 'not_calibrated', deviceText: 'Not calibrated')))),
      ],
      child: MaterialApp(
        builder: (c, w) => MediaQuery(
            data: MediaQuery.of(c).copyWith(disableAnimations: true, textScaler: const TextScaler.linear(2)), child: w!),
        home: const Scaffold(body: SingleChildScrollView(child: SizedBox(width: 280, child: PolarAlignerBlock()))),
      ),
    ));
    await tester.pumpAndSettle();
    expect(tester.takeException(), isNull);
    expect(_key('polar-aligner-resume'), findsOneWidget);
  });

  testWidgets('the status card line: the move while the motors work, Stopped in amber after', (tester) async {
    expect(polarAlignerStatusLine(en, const PolarAlignLive(), southern: false), isNull);
    final moving = polarAlignerStatusLine(en,
        _run(const PolarAlignerRun(state: 'moving', axis: 'azimuth', arcmin: 6.1)), southern: false)!;
    expect((moving.text, moving.color, moving.working), ('Moving azimuth 6.1′ east', AraColors.accentConnected, true));
    final settling = polarAlignerStatusLine(en, _run(const PolarAlignerRun(state: 'settling', seconds: 2)), southern: false)!;
    expect((settling.ring, settling.color), (true, AraColors.accentInfo));
    final stopped = polarAlignerStatusLine(en, _run(const PolarAlignerRun(state: 'stopped', cause: 'diverging')), southern: false)!;
    expect((stopped.text, stopped.color, stopped.working), ('Stopped', AraColors.accentBusy, false));
    expect(polarAlignerStatusLine(en, _run(const PolarAlignerRun(state: 'converged')), southern: false), isNull);
    expect(polarAlignerStatusLine(en, _run(const PolarAlignerRun(state: 'manual')), southern: false), isNull);
  });

  testWidgets('Advanced Mode places the same module in its Polar Align panel, and nothing without one', (tester) async {
    Future<void> pump(PolarAligner device) async {
      await tester.pumpWidget(const SizedBox());
      await tester.pumpWidget(ProviderScope(
        overrides: [
          polarAlignApiProvider.overrideWithValue(_Api()),
          polarAlignerApiProvider.overrideWithValue(_Aligner(device)),
          polarAlignLiveProvider.overrideWith(() => _Live(const PolarAlignLive())),
        ],
        child: const MaterialApp(home: Scaffold(body: SingleChildScrollView(child: PolarAlignPanel()))),
      ));
      await tester.pumpAndSettle();
    }

    await pump(_base);
    expect(find.descendant(of: find.byType(PolarAlignPanel), matching: _key('polar-aligner-block')), findsOneWidget);
    expect(_key('polar-aligner-home'), findsOneWidget);
    await pump(const PolarAligner());
    expect(_key('polar-aligner-block'), findsNothing);
  });
}
