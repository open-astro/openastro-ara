import 'dart:typed_data';

import 'package:dio/dio.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/models/polar_align.dart';
import 'package:openastroara/models/server.dart';
import 'package:openastroara/services/polar_align_api.dart';

/// #2048 — the routine's client against a stub daemon that records each
/// request's receive timeout.
class _Daemon implements HttpClientAdapter {
  final seen = <(String, Duration?)>[];

  @override
  void close({bool force = false}) {}

  @override
  Future<ResponseBody> fetch(RequestOptions options, Stream<Uint8List>? requestStream, Future<void>? cancelFuture) async {
    seen.add(('${options.method} ${options.path}', options.receiveTimeout));
    return ResponseBody.fromString('', 202);
  }
}

/// #2089 — a stub daemon that answers the start plan and records the start's body.
class _PlanDaemon implements HttpClientAdapter {
  _PlanDaemon({this.camera, this.directionTestDeg});

  /// The plan's `camera`; null = an older daemon's plan, with no key.
  final String? camera;

  /// #2081 the plan's `direction_test_deg`; null = no key.
  final double? directionTestDeg;
  Object? startBody;

  @override
  void close({bool force = false}) {}

  @override
  Future<ResponseBody> fetch(RequestOptions options, Stream<Uint8List>? requestStream, Future<void>? cancelFuture) async {
    if (options.path.endsWith('/start-plan')) {
      return ResponseBody.fromString(
        '{"stage":"fine","direction":"west","step_deg":10,"turns":2,"max_sweep_deg":20,'
        '"new_origin":false,"origin_offset_deg":20.3,"sweep_limit_deg":30'
        '${camera == null ? '' : ',"camera":"$camera"'}'
        '${directionTestDeg == null ? '' : ',"direction_test_deg":$directionTestDeg'}}',
        200,
        headers: {
          Headers.contentTypeHeader: [Headers.jsonContentType],
        },
      );
    }
    startBody = options.data;
    return ResponseBody.fromString('', 202);
  }
}

void main() {
  test('the start plan reads where RA stands from the origin, and the start sends new_origin back (#2089)', () async {
    final d = _PlanDaemon();
    final api = PolarAlignApi(const AraServer(hostname: 'x', port: 80), dio: Dio()..httpClientAdapter = d);
    final plan = await api.startPlan(stage: PolarAlignStages.fine);
    expect((plan.newOrigin, plan.originOffsetDeg, plan.sweepLimitDeg), (false, 20.3, 30.0));
    await api.start(plan);
    expect(d.startBody, {'stage': 'fine', 'confirmed': true, 'direction': 'west', 'new_origin': false});
  });

  test('the start plan names the camera the run measures with; a plan without one is the guide camera (#2082)', () async {
    final main = PolarAlignApi(const AraServer(hostname: 'x', port: 80),
        dio: Dio()..httpClientAdapter = _PlanDaemon(camera: PolarAlignCameras.main));
    final older = PolarAlignApi(const AraServer(hostname: 'x', port: 80), dio: Dio()..httpClientAdapter = _PlanDaemon());
    expect((await main.startPlan(stage: PolarAlignStages.coarse)).camera, PolarAlignCameras.main);
    expect((await older.startPlan(stage: PolarAlignStages.fine)).camera, PolarAlignCameras.guide);
  });

  test('Stop and Done wait for the daemon to confirm the mount stopped, past the 5 s default (#2048)', () async {
    final d = _Daemon();
    final api = PolarAlignApi(const AraServer(hostname: 'x', port: 80), dio: Dio()..httpClientAdapter = d);
    await api.stop();
    await api.complete();
    await api.requestCapture();
    expect(d.seen, [
      ('POST /api/v1/equipment/polaralign/stop', PolarAlignApi.endTimeout),
      ('POST /api/v1/equipment/polaralign/complete', PolarAlignApi.endTimeout),
      ('POST /api/v1/equipment/polaralign/capture', null),
    ]);
  });

  test('a Check waits as long as Stop: from the adjust screen it ends the loop first (#2082)', () async {
    final d = _Daemon();
    final api = PolarAlignApi(const AraServer(hostname: 'x', port: 80), dio: Dio()..httpClientAdapter = d);
    PolarAlignStartPlan plan(String stage) => PolarAlignStartPlan.fromJson(<String, dynamic>{
      'stage': stage,
      'direction': 'west',
      'step_deg': 10,
      'turns': 2,
      'max_sweep_deg': 20,
      'new_origin': false,
    }, stage: stage);
    await api.start(plan(PolarAlignStages.check));
    await api.start(plan(PolarAlignStages.fine));
    expect(d.seen, [
      ('POST /api/v1/equipment/polaralign/start', PolarAlignApi.endTimeout),
      ('POST /api/v1/equipment/polaralign/start', null),
    ]);
  });

  test("the end timeout outlasts the daemon's slowest end, a stuck run (#2048)", () {
    // The daemon's defaults (PolarAlignService.MountStop.cs EndWorstCase): each stop command or read 3 s, three
    // commands a round, the 15 s unwind grace, and the end's own confirmation (8 s window, 250 ms poll, two reads,
    // one more round).
    const command = Duration(seconds: 3);
    final round = command * 3;
    final confirmedStop = const Duration(seconds: 8) + const Duration(milliseconds: 250) + command * 2 + round;
    final worstCase = round + const Duration(seconds: 15) + confirmedStop;
    expect(worstCase, const Duration(milliseconds: 47250));
    // Ten seconds at least for tracking off, the session row and the camera release after the stop.
    expect(PolarAlignApi.endTimeout - worstCase, greaterThanOrEqualTo(const Duration(seconds: 10)));
  });

  test('a plan with the motor-direction test sends direction_test back (#2081)', () async {
    final d = _PlanDaemon(directionTestDeg: 0.5);
    final api = PolarAlignApi(const AraServer(hostname: 'x', port: 80), dio: Dio()..httpClientAdapter = d);
    final plan = await api.startPlan(stage: PolarAlignStages.fine);
    expect(plan.directionTestDeg, 0.5);
    await api.start(plan);
    expect(d.startBody, {'stage': 'fine', 'confirmed': true, 'direction': 'west', 'new_origin': false, 'direction_test': true});
    final saved = await PolarAlignApi(const AraServer(hostname: 'x', port: 80), dio: Dio()..httpClientAdapter = _PlanDaemon())
        .startPlan(stage: PolarAlignStages.fine);
    expect(saved.directionTestDeg, 0.0, reason: 'no key: no test (a saved direction, or an older rig)');
  });

  test('the motor direction reads the saved test and its match, and retest posts the forget (#2081)', () async {
    final d = _DirectionDaemon();
    final api = PolarAlignMotorDirectionApi(const AraServer(hostname: 'x', port: 80), dio: Dio()..httpClientAdapter = d);
    final state = await api.get();
    expect((state!.saved!.sign, state.saved!.driverInfo, state.saved!.driverVersion, state.matchesConnectedMount),
        (-1, 'AlpacaBridge iOptron', '6.0.0', true));
    expect(state.saved!.testedUtc, DateTime.utc(2026, 11, 21, 1, 2, 3));
    await api.retest();
    expect(d.posts, ['/api/v1/equipment/polaralign/motor-direction/retest']);
    expect(PolarAlignMotorDirectionState.fromJson({'saved': null, 'matches_connected_mount': null}).saved, isNull);
  });
}

/// #2081 a stub daemon with a saved motor direction.
class _DirectionDaemon implements HttpClientAdapter {
  final posts = <String>[];

  @override
  void close({bool force = false}) {}

  @override
  Future<ResponseBody> fetch(RequestOptions options, Stream<Uint8List>? requestStream, Future<void>? cancelFuture) async {
    if (options.method == 'POST') {
      posts.add(options.path);
      return ResponseBody.fromString('', 204);
    }
    return ResponseBody.fromString(
      '{"saved":{"sign":-1,"driver_info":"AlpacaBridge iOptron","driver_version":"6.0.0",'
      '"tested_utc":"2026-11-21T01:02:03Z"},"matches_connected_mount":true}',
      200,
      headers: {
        Headers.contentTypeHeader: [Headers.jsonContentType],
      },
    );
  }
}
