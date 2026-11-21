import 'dart:convert';
import 'dart:typed_data';

import 'package:dio/dio.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/models/server.dart';
import 'package:openastroara/services/planning_api.dart';

class _StubAdapter implements HttpClientAdapter {
  _StubAdapter(this.statusCode, this.body);
  final int statusCode;
  final Object? body;
  final calls = <String>[];

  @override
  void close({bool force = false}) {}

  @override
  Future<ResponseBody> fetch(RequestOptions options, Stream<Uint8List>? requestStream, Future<void>? cancelFuture) async {
    calls.add('${options.method} ${options.path}');
    return ResponseBody.fromString(body == null ? '' : jsonEncode(body), statusCode,
        headers: {Headers.contentTypeHeader: [Headers.jsonContentType]});
  }
}

void main() {
  const server = AraServer(hostname: '127.0.0.1', port: 5000);
  PlanningApi api(_StubAdapter a) => PlanningApi(server, dio: Dio(BaseOptions(baseUrl: server.baseUrl))..httpClientAdapter = a);
  final record = {'fitted_utc': '2026-10-13T06:00:00Z', 'frames_considered': 3, 'filters': []};

  test('#1140 P2 read hits /api/v1/planning/sky-model; 404 = never fitted', () async {
    final a = _StubAdapter(200, record);
    expect((await api(a).read())!.framesConsidered, 3);
    expect(a.calls, ['GET /api/v1/planning/sky-model']);
    expect(await api(_StubAdapter(404, {'title': 'Not Found'})).read(), isNull);
  });

  test('#1140 P2 recalibrate posts and returns the stored record; clear deletes', () async {
    final a = _StubAdapter(200, record);
    expect((await api(a).recalibrate())!.framesConsidered, 3);
    expect(a.calls, ['POST /api/v1/planning/sky-model/recalibrate']);
    final d = _StubAdapter(204, null);
    await api(d).clear();
    expect(d.calls, ['DELETE /api/v1/planning/sky-model']);
  });

  test('#1140 P2 a server error surfaces', () async {
    expect(api(_StubAdapter(500, {'title': 'boom'})).read(), throwsA(isA<DioException>()));
  });
}
