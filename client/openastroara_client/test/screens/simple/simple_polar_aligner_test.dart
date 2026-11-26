import 'dart:typed_data';

import 'package:dio/dio.dart';
import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/models/polar_align.dart';
import 'package:openastroara/screens/simple/simple_polar_panel.dart';
import 'package:openastroara/screens/simple/simple_shell.dart';
import 'package:openastroara/services/simple_mode_prefs_service.dart';
import 'package:openastroara/state/app_shell_state.dart';
import 'package:openastroara/state/guider/guide_step_state.dart';
import 'package:openastroara/state/rig_setup_state.dart';
import 'package:openastroara/state/server_state.dart';
import 'package:openastroara/state/settings/settings_nav.dart';
import 'package:openastroara/state/simple_mode_state.dart';
import 'package:openastroara/state/sky_atlas/planetarium_selection_state.dart';
import 'package:openastroara/state/ws/ws_providers.dart';
import 'package:openastroara/models/server.dart';
import 'package:openastroara/screens/simple/simple_preview_panel.dart';
import 'package:openastroara/services/polar_align_api.dart';
import 'package:openastroara/state/polar_align/polar_align_state.dart';
import 'package:openastroara/state/polar_align/polar_aligner_state.dart';

import '../../support/polar_align_section.dart';

class _Api implements PolarAlignClient {
  final calls = <String>[];
  @override
  Future<PolarAlignStatus?> getStatus() async => null;
  @override
  Future<void> start() async {}
  @override
  Future<void> stop() async {}
  @override
  Future<void> complete() async => calls.add('complete');
  @override
  Future<void> requestCapture() async {}
  @override
  Future<Uint8List?> getLiveFrame() async => null;
  @override
  void close() {}
}

class _Aligner implements PolarAlignerClient {
  _Aligner(this.device);
  final PolarAligner? device;
  final calls = <String>[];

  /// A refusal the daemon words itself (409 with a detail).
  String? refuse;

  Future<void> _command(String name) async {
    calls.add(name);
    if (refuse case final detail?) {
      final req = RequestOptions(path: '/x');
      throw DioException(
        requestOptions: req,
        type: DioExceptionType.badResponse,
        response: Response(requestOptions: req, statusCode: 409, data: {'detail': detail}),
      );
    }
  }

  @override
  Future<PolarAligner?> get() async => device;
  @override
  Future<void> stop() => _command('stop');
  @override
  Future<void> home() => _command('home');
  @override
  Future<void> stopInRoutine() => _command('stopInRoutine');
  @override
  Future<void> resumeInRoutine() => _command('resumeInRoutine');
  @override
  void close() {}
}

class _Live extends PolarAlignLiveNotifier {
  _Live(this.live);
  final PolarAlignLive live;
  @override
  PolarAlignLive build() => live;
  void set(PolarAlignLive next) => state = next;
}

const _base = PolarAligner(present: true, carrier: 'switch', name: 'Polar base', drivable: true);

/// Adjusting inside the 2′ tolerance: Done is on.
PolarAlignLive _fine(PolarAlignerRun? run) => PolarAlignLive(
      phase: PolarAlignStates.adjusting,
      stage: PolarAlignStages.align,
      altErrorArcmin: -0.7,
      azErrorArcmin: -1.3,
      totalErrorArcmin: 1.5,
      zone: 'green',
      correction: run,
    );

late _Live _liveNotifier;

Future<_Aligner> _pump(WidgetTester tester, PolarAlignLive live, {PolarAligner? device = _base}) async {
  final aligner = _Aligner(device);
  _liveNotifier = _Live(live);
  await tester.pumpWidget(ProviderScope(
    overrides: [
      polarAlignApiProvider.overrideWithValue(_Api()),
      // The profile's section: the rig's copy, a 2′ tolerance (ADR 0015).
      ...polarAlignSectionOverrides(
          FakePolarAlignSection(initial: rigPolarAlign.copyWith(targetToleranceArcmin: 2), loaded: true)),
      polarAlignerApiProvider.overrideWithValue(aligner),
      polarAlignLiveProvider.overrideWith(() => _liveNotifier),
    ],
    child: MaterialApp(
      builder: (c, w) => MediaQuery(data: MediaQuery.of(c).copyWith(disableAnimations: true), child: w!),
      home: const Scaffold(body: Center(child: SingleChildScrollView(child: SimplePolarPanel()))),
    ),
  ));
  await tester.pumpAndSettle();
  return aligner;
}

Finder _key(String k) => find.byKey(Key(k));

double _arrowOpacity(WidgetTester tester, String axis) {
  final opacity = find.ancestor(
      of: find.descendant(of: _key('simple-polar-turn-$axis'), matching: find.byType(Icon)),
      matching: find.byType(Opacity));
  return opacity.evaluate().isEmpty ? 1 : tester.widget<Opacity>(opacity).opacity;
}

void main() {
  testWidgets('the status card has a Polar Aligner line while the motors work, and Stopped after', (tester) async {
    _liveNotifier = _Live(_fine(const PolarAlignerRun(state: 'moving', axis: 'azimuth', arcmin: 6.1, moves: 3)));
    await tester.pumpWidget(ProviderScope(
      overrides: [
        polarAlignApiProvider.overrideWithValue(_Api()),
        polarAlignerApiProvider.overrideWithValue(_Aligner(_base)),
        polarAlignLiveProvider.overrideWith(() => _liveNotifier),
      ],
      child: MaterialApp(
        builder: (c, w) => MediaQuery(data: MediaQuery.of(c).copyWith(disableAnimations: true), child: w!),
        home: const Scaffold(body: Align(alignment: Alignment.topLeft, child: SimpleStatusPill())),
      ),
    ));
    await tester.pump();
    expect(find.textContaining('Polar Aligner'), findsOneWidget);
    expect(find.textContaining('Moving azimuth 6.1′ east'), findsOneWidget);

    _liveNotifier.set(_fine(const PolarAlignerRun(state: 'stopped', cause: 'diverging', moves: 4)));
    await tester.pump();
    expect(find.textContaining('Moving azimuth'), findsNothing);
    expect(find.textContaining('Stopped'), findsOneWidget);
  });

  testWidgets('the Polar Aligner block has its own slot between the settings and the readout', (tester) async {
    await _pump(tester, _fine(const PolarAlignerRun(state: 'measuring')));
    final setup = tester.getRect(_key('simple-polar-setup'));
    final block = tester.getRect(_key('polar-aligner-block'));
    final readout = tester.getRect(_key('simple-polar-total'));
    expect(setup.bottom, lessThanOrEqualTo(block.top));
    expect(block.bottom, lessThanOrEqualTo(readout.top));
  });

  testWidgets('no Polar Aligner on the rig: no block, the arrows and Done as today', (tester) async {
    await _pump(tester, _fine(null), device: const PolarAligner());
    expect(_key('polar-aligner-block'), findsNothing);
    expect(_arrowOpacity(tester, 'alt'), 1);
    expect(_key('simple-polar-done'), findsOneWidget);
  });

  testWidgets('the arrows dim while the motors run and brighten once they stop', (tester) async {
    await _pump(tester, _fine(const PolarAlignerRun(state: 'moving', axis: 'azimuth', arcmin: 1.0)));
    expect(_arrowOpacity(tester, 'alt'), 0.3);
    expect(_arrowOpacity(tester, 'az'), 0.3);
    _liveNotifier.set(_fine(const PolarAlignerRun(state: 'stopped', cause: 'stopped_by_user')));
    await tester.pumpAndSettle();
    expect(_arrowOpacity(tester, 'az'), 1);
  });

  testWidgets('Done steps aside while the motors work and comes back when they converge or stop',
      (tester) async {
    await _pump(tester, _fine(const PolarAlignerRun(state: 'settling', seconds: 2)));
    expect(_key('simple-polar-done'), findsNothing);
    _liveNotifier.set(_fine(const PolarAlignerRun(state: 'converged', moves: 4)));
    await tester.pumpAndSettle();
    expect(_key('simple-polar-done'), findsOneWidget);
    _liveNotifier.set(_fine(const PolarAlignerRun(state: 'stopped', cause: 'diverging')));
    await tester.pumpAndSettle();
    expect(_key('simple-polar-done'), findsOneWidget);
  });

  testWidgets('Stop motors in the panel stops them in the routine', (tester) async {
    final c = await _pump(tester, _fine(const PolarAlignerRun(state: 'moving', axis: 'azimuth', arcmin: 1.0)));
    await tester.ensureVisible(_key('polar-aligner-stop'));
    await tester.tap(_key('polar-aligner-stop'));
    await tester.pumpAndSettle();
    expect(c.calls, ['stopInRoutine']);
  });

  testWidgets('a refused Resume shows the daemon\'s sentence where the panel says what failed', (tester) async {
    final c = await _pump(tester, _fine(const PolarAlignerRun(state: 'stopped', cause: 'stopped_by_user')));
    c.refuse = 'the Polar Aligner is not stopped';
    await tester.ensureVisible(_key('polar-aligner-resume'));
    await tester.tap(_key('polar-aligner-resume'));
    await tester.pumpAndSettle();
    expect(c.calls, ['resumeInRoutine']);
    expect(find.text('the Polar Aligner is not stopped'), findsOneWidget);
  });

  testWidgets('a refused Home shows the daemon\'s sentence', (tester) async {
    final c = await _pump(tester, const PolarAlignLive());
    c.refuse = 'no Polar Aligner is connected';
    await tester.tap(_key('polar-aligner-home'));
    await tester.pumpAndSettle();
    expect(c.calls, ['home']);
    expect(find.text('no Polar Aligner is connected'), findsOneWidget);
  });

  testWidgets('Devices has no Polar Aligner row while the daemon finds none', (tester) async {
    await _pumpDevices(tester, const PolarAligner());
    expect(find.byKey(const Key('simple-gear')), findsOneWidget);
    expect(find.textContaining('Polar Aligner'), findsNothing);
    expect(_key('simple-gear-polaraligner-turned-by'), findsNothing);
  });

  testWidgets('Devices has a Polar Aligner row once the daemon finds one', (tester) async {
    await _pumpDevices(tester, _base);
    expect(find.text('Polar Aligner · Polar base'), findsOneWidget);
  });
}

class _MemPrefs extends SimpleModePrefsService {
  @override
  Future<bool> load() async => true;
  @override
  Future<void> save(bool enabled) async {}
}

class _StartOnLive extends SelectedTabIndexNotifier {
  @override
  int build() => kLiveTabIndex;
}

class _SkyReady extends PlanetariumReadyNotifier {
  @override
  bool build() => true;
}

class _NoSteps extends GuideStepNotifier {
  @override
  List<GuideStep> build() => const [];
}

/// Settings > Devices with the daemon reporting [device] for the Polar Aligner.
Future<void> _pumpDevices(WidgetTester tester, PolarAligner device) async {
  tester.view.physicalSize = const Size(1024, 768);
  tester.view.devicePixelRatio = 1;
  addTearDown(tester.view.reset);
  await tester.pumpWidget(
    ProviderScope(
      overrides: [
        simpleModePrefsProvider.overrideWithValue(_MemPrefs()),
        serverLinkUpProvider.overrideWithValue(true),
        discoveredServersProvider.overrideWith(
          (ref) => const Stream<AraServer>.empty(),
        ),
        rigSetupHydratedProvider.overrideWith((ref) async => false),
        selectedTabIndexProvider.overrideWith(_StartOnLive.new),
        planetariumReadyProvider.overrideWith(_SkyReady.new),
        guideStepsProvider.overrideWith(_NoSteps.new),
        polarAlignerApiProvider.overrideWithValue(_Aligner(device)),
        polarAlignApiProvider.overrideWithValue(_Api()),
      ],
      child: MaterialApp(
        home: const SimpleShell(planetarium: SizedBox(key: Key('sky'))),
      ),
    ),
  );
  await tester.pump();
  await tester.tap(find.byTooltip('Settings'));
  await tester.pump();
  await tester.tap(find.byKey(const Key('simple-menu-nav-Devices')));
  await tester.pumpAndSettle();
}
