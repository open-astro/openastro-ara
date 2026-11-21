import 'dart:convert';
import 'dart:typed_data';

import 'package:dio/dio.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/models/server.dart';
import 'package:openastroara/services/session_live_stack_api.dart';

/// Canned HTTP layer: one scripted answer, and the request it got.
class _Canned implements HttpClientAdapter {
  _Canned(this.statusCode, {this.json, this.bytes, this.headers = const {}});
  final int statusCode;
  final Object? json;
  final List<int>? bytes;
  final Map<String, List<String>> headers;
  RequestOptions? last;

  @override
  Future<ResponseBody> fetch(
    RequestOptions options,
    Stream<Uint8List>? requestStream,
    Future<void>? cancelFuture,
  ) async {
    last = options;
    final body = bytes ?? utf8.encode(json == null ? '' : jsonEncode(json));
    return ResponseBody.fromBytes(
      body,
      statusCode,
      headers: {
        if (json != null) Headers.contentTypeHeader: [Headers.jsonContentType],
        ...headers,
      },
    );
  }

  @override
  void close({bool force = false}) {}
}

SessionLiveStackApi _api(_Canned canned) {
  final dio = Dio(BaseOptions(baseUrl: 'http://192.0.2.10:5000'))
    ..httpClientAdapter = canned;
  return SessionLiveStackApi(
    const AraServer(hostname: '192.0.2.10', port: 5000),
    dio: dio,
  );
}

void main() {
  test(
    'status reads the snake_case body of GET /equipment/camera/live-stack',
    () async {
      final canned = _Canned(
        200,
        json: {
          'enabled': true,
          'unsupported': null,
          'session_id': null,
          'target_name': 'M31',
          'filter_name': 'L',
          'stack_seq': 7,
          'lights_stacked': 6,
          'lights_unregistered': 1,
          'lights_skipped_busy': 2,
          'rejected_samples': 0,
          'width': 1000,
          'height': 800,
          'last_outcome': 'stacked',
          'last_light_at_utc': '2026-11-19T01:02:03.0000000Z',
        },
      );
      final s = await _api(canned).status();
      expect(canned.last!.method, 'GET');
      expect(canned.last!.path, '/api/v1/equipment/camera/live-stack');
      expect(
        (
          s.enabled,
          s.stackSeq,
          s.lightsStacked,
          s.lightsUnregistered,
          s.lightsSkippedBusy,
          s.targetName,
          s.filterName,
          s.lastOutcome,
        ),
        (true, 7, 6, 1, 2, 'M31', 'L', 'stacked'),
      );
      expect(s.supported, isTrue);
    },
  );

  test('a status the daemon marks unsupported is not supported; an absent '
      'key reads as the default', () async {
    final s = await _api(
      _Canned(
        200,
        json: {'enabled': false, 'unsupported': 'not_colour', 'stack_seq': 0},
      ),
    ).status();
    expect(s.supported, isFalse);
    expect(s.unsupported, 'not_colour');
    expect(s.lightsStacked, 0);
  });

  test('a 501 (a camera service without the capability) reads as unsupported, '
      'not as an error', () async {
    final s = await _api(_Canned(501, json: {'title': 'x'})).status();
    expect(s.supported, isFalse);
    expect(s.unsupported, 'not_available');
  });

  test('start, reset and stop are POSTs to their own routes', () async {
    for (final (name, call)
        in <(String, Future<void> Function(SessionLiveStackApi))>[
          ('start', (a) => a.start()),
          ('reset', (a) => a.reset()),
          ('stop', (a) => a.stop()),
        ]) {
      final canned = _Canned(name == 'start' ? 202 : 204);
      await call(_api(canned));
      expect(canned.last!.method, 'POST', reason: name);
      expect(canned.last!.path, '/api/v1/equipment/camera/live-stack/$name');
    }
  });

  test('frame: 204 until a light is stacked is null; 200 carries the JPEG and '
      'its X-Stack-Seq and X-Stack-Lights', () async {
    expect(await _api(_Canned(204)).frame(), isNull);
    final canned = _Canned(
      200,
      bytes: [0xFF, 0xD8, 1],
      headers: {
        'x-stack-seq': ['9'],
        'x-stack-lights': ['8'],
      },
    );
    final f = await _api(canned).frame();
    expect(canned.last!.path, '/api/v1/equipment/camera/live-stack/frame');
    expect(f!.bytes, equals([0xFF, 0xD8, 1]));
    expect((f.seq, f.lights), (9, 8));
  });
}
