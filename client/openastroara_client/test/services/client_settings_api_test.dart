import 'dart:convert';
import 'dart:typed_data';

import 'package:dio/dio.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/models/server.dart';
import 'package:openastroara/services/client_settings_api.dart';

class _FakeAdapter implements HttpClientAdapter {
  _FakeAdapter(this.status, this.body);
  final int status;
  final Object body;
  RequestOptions? last;

  @override
  Future<ResponseBody> fetch(
    RequestOptions options,
    Stream<Uint8List>? requestStream,
    Future<void>? cancelFuture,
  ) async {
    last = options;
    return ResponseBody.fromString(
      jsonEncode(body),
      status,
      headers: {
        Headers.contentTypeHeader: [Headers.jsonContentType],
      },
    );
  }

  @override
  void close({bool force = false}) {}
}

const _server = AraServer(hostname: 'host', port: 5555);

void main() {
  test(
    'merge PATCHes the patch under "settings" and parses the stored view',
    () async {
      final adapter = _FakeAdapter(200, {
        'settings': {'a': 1, 'b': 2},
        'updated_utc': '2026-11-11T10:00:00Z',
      });
      final api = ClientSettingsApi(
        _server,
        dio: Dio()..httpClientAdapter = adapter,
      );

      final stored = await api.merge({'b': 2, 'c': null});

      expect(adapter.last!.method, 'PATCH');
      expect(adapter.last!.path, '/api/v1/client-settings');
      expect(adapter.last!.data, {
        'settings': {'b': 2, 'c': null},
      });
      expect(stored.settings, {'a': 1, 'b': 2});
      expect(stored.updatedUtc, DateTime.utc(2026, 11, 11, 10));
    },
  );

  test(
    'an older daemon answering 404 or 405 reads as merge-unsupported',
    () async {
      for (final status in [404, 405]) {
        final api = ClientSettingsApi(
          _server,
          dio: Dio()
            ..httpClientAdapter = _FakeAdapter(status, {'title': 'nope'}),
        );
        Object? error;
        try {
          await api.merge({'a': 1});
        } catch (e) {
          error = e;
        }
        expect(error, isNotNull);
        expect(isMergeUnsupported(error!), isTrue, reason: '$status');
      }
      expect(isMergeUnsupported(StateError('x')), isFalse);
      expect(
        isMergeUnsupported(
          DioException(
            requestOptions: RequestOptions(),
            response: Response(
              requestOptions: RequestOptions(),
              statusCode: 400,
            ),
          ),
        ),
        isFalse,
      );
    },
  );
}
