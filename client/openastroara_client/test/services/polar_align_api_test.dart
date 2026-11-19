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
  Object? startBody;

  @override
  void close({bool force = false}) {}

  @override
  Future<ResponseBody> fetch(RequestOptions options, Stream<Uint8List>? requestStream, Future<void>? cancelFuture) async {
    if (options.path.endsWith('/start-plan')) {
      return ResponseBody.fromString(
        '{"stage":"fine","direction":"west","step_deg":10,"turns":2,"max_sweep_deg":20,'
        '"new_origin":false,"origin_offset_deg":20.3,"sweep_limit_deg":30}',
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

  test('Stop and Done wait for the daemon to confirm the mount stopped, past the 5 s default (#2048)', () async {
    final d = _Daemon();
    final api = PolarAlignApi(const AraServer(hostname: 'x', port: 80), dio: Dio()..httpClientAdapter = d);
    await api.stop();
    await api.complete();
    await api.next();
    expect(d.seen, [
      ('POST /api/v1/equipment/polaralign/stop', const Duration(seconds: 30)),
      ('POST /api/v1/equipment/polaralign/complete', const Duration(seconds: 30)),
      ('POST /api/v1/equipment/polaralign/next', null),
    ]);
  });
}
