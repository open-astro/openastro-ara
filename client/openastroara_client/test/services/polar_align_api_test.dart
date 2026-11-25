import 'dart:typed_data';

import 'package:dio/dio.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/models/polar_align.dart';
import 'package:openastroara/models/server.dart';
import 'package:openastroara/services/polar_align_api.dart';
import 'package:openastroara/services/refusal.dart';

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

/// A stub daemon that records each request's body, and refuses when told to.
class _StartDaemon implements HttpClientAdapter {
  _StartDaemon({this.refusal});

  /// A 409 problem body to answer with; null = 202.
  final String? refusal;
  final bodies = <Object?>[];

  @override
  void close({bool force = false}) {}

  @override
  Future<ResponseBody> fetch(RequestOptions options, Stream<Uint8List>? requestStream, Future<void>? cancelFuture) async {
    bodies.add(options.data);
    if (refusal case final body?) {
      return ResponseBody.fromString(body, 409, headers: {
        Headers.contentTypeHeader: ['application/problem+json'],
      });
    }
    return ResponseBody.fromString('', 202);
  }
}

void main() {
  test('a start posts only its stage: no confirmation, direction or plan goes with it', () async {
    final d = _StartDaemon();
    final api = PolarAlignApi(const AraServer(hostname: 'x', port: 80), dio: Dio()..httpClientAdapter = d);
    await api.start();
    await api.start(stage: PolarAlignStages.check);
    expect(d.bodies, [
      <String, dynamic>{},
      {'stage': 'check'},
    ]);
  });

  test('a start the rig refuses for a mount away from Home is the polar-align-not-home Refusal', () async {
    final d = _StartDaemon(
      refusal: '{"type":"about:blank","title":"The mount is not at Home","status":409,'
          '"detail":"Send the mount Home first","reason":"polar-align-not-home"}',
    );
    final api = PolarAlignApi(const AraServer(hostname: 'x', port: 80), dio: Dio()..httpClientAdapter = d);
    await expectLater(
      api.start(),
      throwsA(isA<Refusal>().having((r) => r.cause, 'cause', RefusalCause.polarAlignNotHome)),
    );
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
    await api.start(stage: PolarAlignStages.check);
    await api.start(stage: PolarAlignStages.fine);
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
