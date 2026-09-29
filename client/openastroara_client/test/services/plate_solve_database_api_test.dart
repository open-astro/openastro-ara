import 'dart:convert';
import 'dart:typed_data';

import 'package:dio/dio.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/models/server.dart';
import 'package:openastroara/services/plate_solve_database_api.dart';

class _StubAdapter implements HttpClientAdapter {
  _StubAdapter(this.statusCode, this.body, {this.fail = false});
  final int statusCode;
  final Object? body;
  final bool fail;
  String? lastPath;

  @override
  void close({bool force = false}) {}

  @override
  Future<ResponseBody> fetch(
    RequestOptions options,
    Stream<Uint8List>? requestStream,
    Future<void>? cancelFuture,
  ) async {
    lastPath = options.path;
    if (fail) {
      throw DioException.connectionError(
        requestOptions: options,
        reason: 'no route to host',
      );
    }
    return ResponseBody.fromString(
      jsonEncode(body),
      statusCode,
      headers: {
        Headers.contentTypeHeader: [Headers.jsonContentType],
      },
    );
  }
}

PlateSolveDatabaseApi _api(_StubAdapter adapter) => PlateSolveDatabaseApi(
  const AraServer(hostname: 'x', port: 80),
  dio: Dio()..httpClientAdapter = adapter,
);

void main() {
  test('reads the status from GET /api/v1/platesolve/database', () async {
    final adapter = _StubAdapter(200, {
      'configured_path': '/var/lib/astap',
      'effective_path': '/var/lib/astap',
      'file_count': 3,
      'databases': ['d80', 'w08'],
      'solver_path': '/usr/bin/astap_cli',
      'solver_found': true,
    });

    final s = await _api(adapter).fetch();

    expect(adapter.lastPath, '/api/v1/platesolve/database');
    expect(s, isNotNull);
    expect(s!.fileCount, 3);
    expect(s.databases, ['d80', 'w08']);
    expect(s.hasDatabase, isTrue);
  });

  test('a daemon without the route (404) reads as unknown', () async {
    final s = await _api(_StubAdapter(404, {'title': 'Not Found'})).fetch();
    expect(s, isNull);
  });

  test('an unreachable daemon reads as unknown, not an error', () async {
    final s = await _api(_StubAdapter(200, null, fail: true)).fetch();
    expect(s, isNull);
  });

  test('a non-object body reads as unknown', () async {
    final s = await _api(_StubAdapter(200, ['nope'])).fetch();
    expect(s, isNull);
  });
}
