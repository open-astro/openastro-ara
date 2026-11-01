import 'dart:async';

import 'package:dio/dio.dart';
import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/models/equipment_device_status.dart';
import 'package:openastroara/models/mount_status.dart';
import 'package:openastroara/screens/simple/simple_target_panel.dart';
import 'package:openastroara/services/autofocus_api.dart' show AutofocusJob;
import 'package:openastroara/services/center_api.dart';
import 'package:openastroara/state/equipment/mount_state.dart';
import 'package:openastroara/state/sky_atlas/planetarium_selection_state.dart';

MountStatus _mount({String runtime = 'tracking', bool parked = false}) => MountStatus(
      deviceId: 'm',
      name: 'Mount',
      connectionState: EquipmentConnectionState.connected,
      capabilities: null,
      runtimeState: runtime,
      rightAscensionHours: 0,
      declinationDegrees: 0,
      tracking: true,
      parked: parked,
      atHome: false,
    );

const _m31 = PlanetariumSelection(
    name: 'M31', kind: 'Galaxy', raHours: 0.73, decDeg: 41.4, raJ2000Hours: 0.71, decJ2000Deg: 41.27, altDeg: 62.4, maxAltDeg: 74.2);
final _m33Below = PlanetariumSelection(
    name: 'M33', kind: 'Galaxy', raHours: 1.6, decDeg: 30.8, raJ2000Hours: 1.56, decJ2000Deg: 30.66, altDeg: -12, maxAltDeg: 60, rise: DateTime(2026, 11, 2, 22, 40));
const _southern = PlanetariumSelection(
    name: 'NGC 104', kind: 'Cluster', raHours: 0.4, decDeg: -72, raJ2000Hours: 0.4, decJ2000Deg: -72, altDeg: -40, maxAltDeg: -20);

class _Center implements CenterClient {
  _Center(this.calls);
  final List<String> calls;
  String state = 'running';
  @override
  Future<AutofocusJob> start(double ra, double dec) async {
    calls.add('center $ra $dec');
    return const AutofocusJob(jobId: 'j1', state: 'running', total: 3);
  }

  @override
  Future<AutofocusJob?> job(String id) async => AutofocusJob(jobId: id, state: state, done: 1, total: 3,
      errorMessage: state == 'failed' ? 'Too few stars' : null);
  @override
  Future<void> cancel(String id) async => calls.add('cancel $id');
  @override
  void close() {}
}

class _Mount extends MountNotifier {
  _Mount(this.status, {this.refuse = false});
  final MountStatus? status;
  final bool refuse;
  final calls = <String>[];
  @override
  Future<MountStatus?> build() async => status;
  @override
  Future<bool> unpark() async {
    calls.add('unpark');
    return true;
  }

  @override
  Future<bool> slewTo(double raHours, double decDegrees, {bool sync = false, bool force = false}) async {
    calls.add('slew $raHours $decDegrees${force ? ' force' : ''}');
    if (refuse && !force) {
      throw DioException(
        requestOptions: RequestOptions(path: '/slew'),
        response: Response(
            requestOptions: RequestOptions(path: '/slew'),
            statusCode: 409,
            data: {'title': 'run_in_progress', 'detail': 'A sequence is running'}),
      );
    }
    return true;
  }

  @override
  Future<bool> abortSlew() async {
    calls.add('abort');
    return true;
  }
}

class _Sel extends PlanetariumSelectionNotifier {
  _Sel(this.value);
  final PlanetariumSelection? value;
  @override
  PlanetariumSelection? build() => value;
}

Future<(_Mount, ProviderContainer)> _pump(WidgetTester tester, Widget child,
    {PlanetariumSelection? sel, MountStatus? mount, bool refuse = false, _Center? center}) async {
  final m = _Mount(mount, refuse: refuse);
  final container = ProviderContainer(overrides: [
    mountProvider.overrideWith(() => m),
    centerApiProvider.overrideWithValue(center ?? _Center(m.calls)),
    planetariumSelectionProvider.overrideWith(() => _Sel(sel)),
  ]);
  addTearDown(container.dispose);
  await tester.pumpWidget(UncontrolledProviderScope(
    container: container,
    child: MaterialApp(home: Scaffold(body: Center(child: child))),
  ));
  await tester.pumpAndSettle();
  return (m, container);
}

InkResponse _shutter(WidgetTester t) => t.widget<InkResponse>(find.byKey(const Key('simple-shutter')));

void main() {
  test('simpleTargetSummary: the name, or None', () {
    expect(simpleTargetSummary(null), 'None');
    expect(simpleTargetSummary(_m31), 'M31');
  });

  test('simpleTargetStatus: up, below with its rise, never rises', () {
    expect(simpleTargetStatus(_m31), '62° up · max 74°');
    expect(simpleTargetStatus(_m33Below), 'Below your horizon · rises 22:40');
    expect(simpleTargetStatus(_southern), 'Never rises here');
  });

  test('a page selection parses; no position or nothing selected is null', () {
    final s = PlanetariumSelection.tryFromJson(
        {'name': 'M31', 'kind': 'Galaxy', 'raHours': 0.73, 'decDeg': 41.4, 'raJ2000Hours': 0.71, 'decJ2000Deg': 41.27,
         'altDeg': 62.4, 'maxAltDeg': 74.2, 'riseMs': null});
    expect(s!.name, 'M31');
    expect(s.raJ2000Hours, 0.71);
    expect(s.belowHorizon, isFalse);
    expect(PlanetariumSelection.tryFromJson(null), isNull);
    expect(PlanetariumSelection.tryFromJson({'name': 'x'}), isNull);
  });

  group('the shutter goes to the selection', () {
    testWidgets('off with nothing selected', (tester) async {
      await _pump(tester, const SimpleTargetShutter(), mount: _mount());
      expect(_shutter(tester).onTap, isNull);
    });
    testWidgets('off with no mount', (tester) async {
      await _pump(tester, const SimpleTargetShutter(), sel: _m31);
      expect(_shutter(tester).onTap, isNull);
    });
    testWidgets('off below the horizon', (tester) async {
      await _pump(tester, const SimpleTargetShutter(), sel: _m33Below, mount: _mount());
      expect(_shutter(tester).onTap, isNull);
    });
    testWidgets('goes to it and centres it: unpark, slew, then the centring job (J2000)', (tester) async {
      final (m, c) = await _pump(tester, const SimpleTargetShutter(), sel: _m31, mount: _mount(parked: true));
      await tester.tap(find.byKey(const Key('simple-shutter')));
      await tester.pump();
      await tester.pump();
      expect(m.calls, ['unpark', 'slew 0.73 41.4', 'center 0.71 41.27']);
      expect(c.read(simpleGoToProvider), isA<SimpleCentering>());
      expect(find.bySemanticsLabel('Stop'), findsOneWidget, reason: 'the red square while it centres');
      await tester.tap(find.byKey(const Key('simple-shutter')));
      await tester.pump();
      expect(m.calls.skip(3), ['cancel j1', 'abort']);
      expect(c.read(simpleGoToProvider), isNull);
    });
    testWidgets('a finished centring says so; a failed one says why', (tester) async {
      final calls = <String>[];
      final center = _Center(calls);
      final (_, c) = await _pump(tester, const SimpleTargetPanel(), sel: _m31, mount: _mount(), center: center);
      unawaited(c.read(simpleGoToProvider.notifier).goTo());
      await tester.pump();
      center.state = 'complete';
      await tester.pump(const Duration(seconds: 2));
      expect(find.text('Centered on M31'), findsOneWidget);
      unawaited(c.read(simpleGoToProvider.notifier).goTo());
      await tester.pump();
      center.state = 'failed';
      await tester.pump(const Duration(seconds: 2));
      expect(find.textContaining('Too few stars'), findsOneWidget);
    });
    testWidgets('while it slews, the square aborts', (tester) async {
      final (m, _) = await _pump(tester, const SimpleTargetShutter(), sel: _m31, mount: _mount(runtime: 'slewing'));
      expect(find.bySemanticsLabel('Stop'), findsOneWidget);
      await tester.tap(find.byKey(const Key('simple-shutter')));
      await tester.pumpAndSettle();
      expect(m.calls, ['abort']);
    });
  });

  group('the panel', () {
    testWidgets('nothing selected: the hint; a search reaches the planetarium', (tester) async {
      final (_, c) = await _pump(tester, const SimpleTargetPanel(), mount: _mount());
      expect(find.text('Tap an object in the sky, or search.'), findsOneWidget);
      final got = <String?>[];
      c.listen(planetariumSearchProvider, (_, q) => got.add(q));
      await tester.enterText(find.byKey(const Key('simple-target-search')), 'M 31');
      await tester.testTextInput.receiveAction(TextInputAction.search);
      await tester.pump();
      expect(got, ['M 31']);
    });
    testWidgets('the selection, and why it is blocked', (tester) async {
      await _pump(tester, const SimpleTargetPanel(), sel: _m33Below, mount: _mount());
      expect(find.text('M33'), findsOneWidget);
      expect(find.text('Galaxy'), findsOneWidget);
      expect(find.text('Below your horizon · rises 22:40'), findsOneWidget);
    });
    testWidgets('a running sequence refuses: Slew anyway forces it', (tester) async {
      final calls = <String>[];
      final center = _Center(calls)..state = 'complete';
      final (m, c) = await _pump(
        tester,
        const Column(mainAxisSize: MainAxisSize.min, children: [SimpleTargetPanel(), SimpleTargetShutter()]),
        sel: _m31,
        mount: _mount(),
        refuse: true,
        center: center,
      );
      await tester.tap(find.byKey(const Key('simple-shutter')));
      await tester.pumpAndSettle();
      expect(find.text('Slew anyway'), findsOneWidget);
      await tester.tap(find.byKey(const Key('simple-target-force')));
      await tester.pumpAndSettle();
      expect(m.calls, ['slew 0.73 41.4', 'slew 0.73 41.4 force']);
      expect(calls, ['center 0.71 41.27'], reason: 'then it centres');
      await tester.pump(const Duration(seconds: 2));
      expect(c.read(simpleGoToProvider), isA<SimpleCentered>());
    });
  });
}
