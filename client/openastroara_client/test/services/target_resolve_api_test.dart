import 'dart:convert';
import 'dart:typed_data';

import 'package:dio/dio.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/models/server.dart';
import 'package:openastroara/services/target_resolve_api.dart';

class _StubAdapter implements HttpClientAdapter {
  _StubAdapter(this.jsonBody, {this.throwError = false});
  final Object jsonBody;
  final bool throwError;
  RequestOptions? lastRequest;

  @override
  void close({bool force = false}) {}

  @override
  Future<ResponseBody> fetch(
    RequestOptions options,
    Stream<Uint8List>? requestStream,
    Future<void>? cancelFuture,
  ) async {
    lastRequest = options;
    if (throwError) {
      throw DioException.connectionError(
        requestOptions: options,
        reason: 'refused',
      );
    }
    return ResponseBody.fromString(
      jsonEncode(jsonBody),
      200,
      headers: {
        Headers.contentTypeHeader: [Headers.jsonContentType],
      },
    );
  }
}

TargetResolveApi _api(_StubAdapter adapter) {
  final dio = Dio(BaseOptions(baseUrl: 'http://ara.test'))
    ..httpClientAdapter = adapter;
  return TargetResolveApi(
    const AraServer(hostname: 'ara.test', port: 80),
    dio: dio,
  );
}

void main() {
  test(
    'resolve sends the name as a query and parses the #1267 wire shape',
    () async {
      final adapter = _StubAdapter(const {
        'query': 'ab cas',
        'status': 'resolved',
        'source': 'index',
        'main_id': 'AB Cas',
        'aliases': ['HIP 12235', 'V* AB Cas'],
        'ra_deg': 39.381465,
        'dec_deg': 71.304515,
        'object_type': 'V*',
        'magnitude': 10.21,
        'message': null,
      });
      final r = await _api(adapter).resolve('ab cas');
      expect(adapter.lastRequest!.path, '/api/v1/targets/resolve');
      expect(adapter.lastRequest!.queryParameters, {'name': 'ab cas'});
      expect(r.resolved, isTrue);
      expect(r.mainId, 'AB Cas');
      expect(r.aliases, ['HIP 12235', 'V* AB Cas']);
      expect(r.raDeg, closeTo(39.381465, 1e-9));
      expect(r.decDeg, closeTo(71.304515, 1e-9));
      expect(
        r.provenance,
        'Offline index · V* · mag 10.2 · also HIP 12235, V* AB Cas',
      );
    },
  );

  test('a miss keeps its status and message and is not resolved', () async {
    final r = await _api(
      _StubAdapter(const {
        'query': 'x',
        'status': 'offline',
        'source': '',
        'main_id': null,
        'aliases': [],
        'ra_deg': null,
        'dec_deg': null,
        'object_type': null,
        'magnitude': null,
        'message': 'Not in the offline index. Simbad could not be reached: this name needs internet.',
      }),
    ).resolve('x');
    expect(r.resolved, isFalse);
    expect(r.offline, isTrue);
    expect(r.message, contains('needs internet'));
  });

  test(
    'an unreachable daemon or a non-JSON body answers offline, never throws',
    () async {
      final down = await _api(_StubAdapter(const {}, throwError: true))
          .resolve('x');
      expect(down.offline, isTrue);
      expect(down.message, contains('rig'));
      final odd = await _api(_StubAdapter('nonsense')).resolve('x');
      expect(odd.offline, isTrue);
    },
  );

  test('provenance names the source and truncates long alias lists', () {
    const r = TargetResolution(
      status: 'resolved',
      source: 'cache',
      mainId: 'V* AB Cas',
      aliases: ['a', 'b', 'c', 'd', 'e'],
      raDeg: 1,
      decDeg: 2,
    );
    expect(r.provenance, 'Simbad (saved on the rig) · also a, b, c, d, …');
    expect(
      const TargetResolution(
        status: 'resolved',
        source: 'simbad',
        mainId: 'x',
        raDeg: 1,
        decDeg: 2,
      ).provenance,
      'Simbad',
    );
  });
}
