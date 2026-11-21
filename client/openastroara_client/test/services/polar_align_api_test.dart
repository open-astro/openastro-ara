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
  _PlanDaemon({this.camera});

  /// The plan's `camera`; null = an older daemon's plan, with no key.
  final String? camera;
  Object? startBody;

  @override
  void close({bool force = false}) {}

  @override
  Future<ResponseBody> fetch(RequestOptions options, Stream<Uint8List>? requestStream, Future<void>? cancelFuture) async {
    if (options.path.endsWith('/start-plan')) {
      return ResponseBody.fromString(
        '{"stage":"fine","direction":"west","step_deg":10,"turns":2,"max_sweep_deg":20,'
        '"new_origin":false,"origin_offset_deg":20.3,"sweep_limit_deg":30'
        '${camera == null ? '' : ',"camera":"$camera"'}}',
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

  test('an older rig does not take a hand turn; a hand-turn start sends hand_turn, a motor start never does (#2083)', () async {
    final d = _PlanDaemon();
    final api = PolarAlignApi(const AraServer(hostname: 'x', port: 80), dio: Dio()..httpClientAdapter = d);
    final plan = await api.startPlan(stage: PolarAlignStages.fine);
    expect(plan.handTurnAvailable, isFalse, reason: 'the stub plan has no hand_turn_available: an older rig');
    await api.start(plan.byHand());
    expect(d.startBody, {'stage': 'fine', 'confirmed': true, 'direction': 'west', 'new_origin': false, 'hand_turn': true});
    expect(PolarAlignStartPlan.fromJson({'direction': 'east', 'hand_turn_available': true}).handTurnAvailable, isTrue);
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
}
