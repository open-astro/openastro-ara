import 'dart:convert';
import 'dart:typed_data';

import 'package:dio/dio.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/models/server.dart';
import 'package:openastroara/services/guiding_autotune_api.dart';

class _FakeAdapter implements HttpClientAdapter {
  _FakeAdapter(this.handler);

  final ResponseBody Function(RequestOptions options) handler;
  RequestOptions? lastRequest;

  @override
  Future<ResponseBody> fetch(RequestOptions options,
      Stream<Uint8List>? requestStream, Future<void>? cancelFuture) async {
    lastRequest = options;
    return handler(options);
  }

  @override
  void close({bool force = false}) {}
}

ResponseBody _json(Object body, {int status = 200}) => ResponseBody.fromString(
      jsonEncode(body),
      status,
      headers: {
        Headers.contentTypeHeader: [Headers.jsonContentType],
      },
    );

const _server = AraServer(hostname: 'host', port: 5555);

GuidingAutoTuneApi _api(_FakeAdapter adapter) => GuidingAutoTuneApi(
      _server,
      dio: Dio()..httpClientAdapter = adapter,
    );

Map<String, dynamic> _status({String state = 'Proposed'}) => <String, dynamic>{
      'session_id': 'session-1',
      'state': state,
      'progress': .75,
      'current_step': 'proposal ready',
      'behavior_class': 'HarmonicLike',
      'behavior_confidence': .84,
      'telemetry_samples': 120,
      'baseline_score': 1.0,
      'best_score': .7,
      'can_apply': state == 'Proposed',
      'can_rollback': true,
      'warnings': <String>[],
      'started_at_utc': '2026-07-31T00:00:00Z',
      'updated_at_utc': '2026-07-31T00:01:00Z',
    };

void main() {
  test('capabilities parses snake-case response', () async {
    final api = _api(_FakeAdapter((_) => _json(<String, dynamic>{
          'enabled': true,
          'connected': true,
          'has_telemetry': true,
          'can_analyze': true,
          'can_apply': false,
          'guide_rate_changes_supported': true,
          'locked_reasons': ['main camera unavailable'],
        })));

    final result = await api.getCapabilities();
    expect(result.connected, isTrue);
    expect(result.guideRateChangesSupported, isTrue);
    expect(result.lockedReasons, ['main camera unavailable']);
  });

  test('start posts bounded dry-run request and parses status', () async {
    final adapter = _FakeAdapter((_) => _json(_status()));
    final api = _api(adapter);

    final result = await api.start(
      depth: 'deep',
      dryRun: false,
      useMainCameraValidation: true,
    );

    expect(result.sessionId, 'session-1');
    expect(result.state, 'Proposed');
    final body = adapter.lastRequest!.data as Map<String, dynamic>;
    expect(body['depth'], 'deep');
    expect(body['dry_run'], isFalse);
    expect(body['use_main_camera_validation'], isTrue);
  });

  test('status endpoint and report endpoint parse responses', () async {
    final adapter = _FakeAdapter((request) {
      if (request.path.endsWith('/report')) {
        return _json(<String, dynamic>{
          'session_id': 'session-1',
          'content_type': 'text/markdown',
          'markdown': '# report',
        });
      }
      return _json(_status(state: 'Completed'));
    });
    final api = _api(adapter);

    expect((await api.getLatest()).state, 'Completed');
    expect((await api.getReport()).markdown, '# report');
  });

  test('cancel apply and rollback use POST endpoints', () async {
    final adapter = _FakeAdapter((request) => _json(_status(state: 'RolledBack')));
    final api = _api(adapter);

    await api.cancel();
    expect(adapter.lastRequest!.path, endsWith('/sessions/latest/cancel'));
    await api.apply();
    expect(adapter.lastRequest!.path, endsWith('/sessions/latest/apply'));
    await api.rollback();
    expect(adapter.lastRequest!.path, endsWith('/sessions/latest/rollback'));
  });
}
