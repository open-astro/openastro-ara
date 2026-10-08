import 'dart:async';
import 'dart:io';

import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/models/server.dart';
import 'package:openastroara/models/ws_event.dart';
import 'package:openastroara/screens/tabs/imaging_tab.dart';
import 'package:openastroara/widgets/imaging/guiding_strip.dart';
import 'package:openastroara/services/ws_event_stream.dart';
import 'package:openastroara/state/imaging/capture_progress_state.dart';
import 'package:openastroara/state/imaging/exposure_activity_state.dart';
import 'package:openastroara/state/saved_server_state.dart';
import 'package:openastroara/state/ws/ws_providers.dart';

/// The Live tab's wiring between Take One's own capture card and the rail's
/// exposure timer banner (PR #1269): the banner steps aside while Take One is
/// capturing, and a successful Cancel drops the exposure activity so the
/// banner doesn't take over counting the sub that was just aborted.

const _abortPath = '/api/v1/equipment/camera/exposure/abort';

WsEvent _started(String frameId, num secs) => WsEvent(
  type: 'camera.exposure_started',
  ts: DateTime.utc(2026, 10, 4),
  seq: 1,
  payload: {'frame_id': frameId, 'exposure_sec': secs, 'kind': 'light'},
);

class _Rig {
  _Rig(this.container, this.ws);
  final ProviderContainer container;
  final StreamController<WsEvent> ws;

  CaptureProgressNotifier get progress =>
      container.read(captureProgressProvider.notifier);
}

Future<_Rig> _pumpTab(WidgetTester tester, {AraServer? server}) async {
  tester.view.physicalSize = const Size(1400, 1000);
  tester.view.devicePixelRatio = 1;
  addTearDown(tester.view.reset);
  final ws = StreamController<WsEvent>.broadcast();
  addTearDown(ws.close);
  final container = ProviderContainer(
    overrides: [
      activeServerProvider.overrideWithValue(server),
      // No real socket: the tab's WS-driven state is fed from [ws].
      wsEventStreamProvider.overrideWith((ref) => null),
      wsEventsProvider.overrideWith((ref) => ws.stream),
      wsConnectionStateProvider.overrideWith(
        (ref) => Stream.value(WsConnectionState.connected),
      ),
    ],
  );
  await tester.pumpWidget(
    UncontrolledProviderScope(
      container: container,
      child: const MaterialApp(home: Scaffold(body: ImagingTab())),
    ),
  );
  await tester.pump();
  return _Rig(container, ws);
}

/// Unmount first so the banner ticker and the guiding strip's poll are
/// cancelled before the pending-timer check, then drop the container (which
/// cancels the exposure activity's watchdog).
Future<void> _teardown(WidgetTester tester, _Rig rig) async {
  rig.progress.reset();
  await tester.pumpWidget(const SizedBox());
  rig.container.dispose();
  // With a server set, the rail's own providers (equipment status, …) fired
  // requests from the fake-async zone, which never reach the socket; run
  // the clock past Dio's 3 s connect timeout so they fail out.
  await tester.pump(const Duration(seconds: 5));
}

final _banner = find.text('Exposing · Light');

void main() {
  testWidgets('the guiding strip is sized from the Live tab height', (tester) async {
    final rig = await _pumpTab(tester);
    final tab = tester.getSize(find.byType(ImagingTab)).height;
    final strip = tester.widget<GuidingStrip>(find.byType(GuidingStrip));
    expect(strip.graphHeight, GuidingStrip.graphHeightFor(tab));
    expect(strip.graphHeight, isNot(GuidingStrip.defaultGraphHeight));
    await _teardown(tester, rig);
  });

  testWidgets('the rail exposure timer shows for a daemon exposure, and '
      'steps aside while Take One is capturing', (tester) async {
    final rig = await _pumpTab(tester);
    rig.ws.add(_started('seq-1', 120));
    await tester.pump();
    await tester.pump();
    expect(rig.container.read(exposureActivityProvider), isNotNull);
    expect(
      _banner,
      findsOneWidget,
      reason: 'a sequence sub with no Take One gets the rail timer',
    );

    rig.progress.beginExposing(const Duration(seconds: 10));
    await tester.pump();
    expect(
      find.text('Exposing 10s… 0%'),
      findsOneWidget,
      reason: "Take One's own card is up",
    );
    expect(
      _banner,
      findsNothing,
      reason: 'the banner hides while Take One is capturing',
    );

    rig.progress.reset();
    await tester.pump();
    expect(
      _banner,
      findsOneWidget,
      reason: 'and returns once Take One is no longer capturing',
    );

    await _teardown(tester, rig);
  });

  testWidgets('a successful Cancel clears the exposure activity so the '
      'banner does not take over the aborted sub', (tester) async {
    // A real loopback daemon stand-in: CameraExposureApi builds its own Dio,
    // so the abort POST has to reach a socket. The test binding mocks every
    // HttpClient to answer 400, so lift that for this test only.
    final savedHttp = HttpOverrides.current;
    HttpOverrides.global = null;
    addTearDown(() => HttpOverrides.global = savedHttp);
    final hits = <String>[];
    final server = (await tester.runAsync(() async {
      final s = await HttpServer.bind(InternetAddress.loopbackIPv4, 0);
      s.listen((req) async {
        hits.add('${req.method} ${req.uri.path}');
        req.response.statusCode = req.uri.path == _abortPath
            ? HttpStatus.ok
            : HttpStatus.notFound;
        await req.response.close();
      });
      return s;
    }))!;
    addTearDown(() => server.close(force: true));

    final rig = await _pumpTab(
      tester,
      server: AraServer(hostname: '127.0.0.1', port: server.port),
    );
    // A Take One in flight: the daemon announced the exposure and the card is
    // counting it down.
    rig.ws.add(_started('take-one-1', 30));
    rig.progress.beginExposing(const Duration(seconds: 30));
    await tester.pump();
    await tester.pump();
    expect(rig.container.read(exposureActivityProvider), isNotNull);
    expect(_banner, findsNothing);

    // Tapped inside runAsync so the abort POST runs on real I/O; then pump
    // until the cancel handler has finished with the response.
    await tester.runAsync(() => tester.tap(find.text('Cancel')));
    for (
      var i = 0;
      i < 250 && rig.container.read(captureProgressProvider).isCapturing;
      i++
    ) {
      await tester.pump();
      await tester.runAsync(
        () => Future<void>.delayed(const Duration(milliseconds: 20)),
      );
    }
    await tester.pump();

    expect(hits, ['POST $_abortPath']);
    expect(rig.container.read(captureProgressProvider).isCapturing, isFalse);
    expect(
      find.byType(SnackBar),
      findsNothing,
      reason: 'the abort was accepted',
    );
    expect(
      rig.container.read(exposureActivityProvider),
      isNull,
      reason: 'a cancelled exposure must not leave the activity running',
    );
    expect(
      _banner,
      findsNothing,
      reason: 'the rail timer must not resume counting the aborted sub',
    );

    await _teardown(tester, rig);
  });
}
