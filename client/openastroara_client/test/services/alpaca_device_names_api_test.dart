import 'dart:convert';
import 'dart:typed_data';

import 'package:dio/dio.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/models/server.dart';
import 'package:openastroara/services/alpaca_device_names_api.dart';
import 'package:openastroara/state/saved_server_state.dart';
import 'package:openastroara/widgets/guider/guider_setup_wizard.dart';

/// Stubs Dio's transport (the TimeSyncApi test pattern) and records the
/// request so the daemon route + query can be asserted.
class _StubAdapter implements HttpClientAdapter {
  _StubAdapter(this.jsonBody, {this.status = 200});
  final Object jsonBody;
  final int status;
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
    return ResponseBody.fromString(
      jsonEncode(jsonBody),
      status,
      headers: {
        Headers.contentTypeHeader: [Headers.jsonContentType],
      },
    );
  }
}

void main() {
  const server = AraServer(hostname: 'rig', port: 5555);

  test('fetchNames dials the DAEMON route with host/port, never the Alpaca '
      'host (#1067)', () async {
    final adapter = _StubAdapter(const {
      'names': {'camera/1': 'ZWO ASI290MM Mini'},
    });
    final api = AlpacaDeviceNamesApi(server,
        dio: Dio(BaseOptions(baseUrl: server.baseUrl))
          ..httpClientAdapter = adapter);
    final names = await api.fetchNames('192.168.1.118', 6800);
    expect(names, {'camera/1': 'ZWO ASI290MM Mini'});
    final req = adapter.lastRequest!;
    expect(req.method, 'GET');
    expect(req.path, '/api/v1/equipment/guider/alpacadevicenames');
    expect(req.queryParameters, {'host': '192.168.1.118', 'port': 6800});
    expect(req.uri.host, 'rig', reason: 'the request goes to the daemon');
    expect(req.uri.port, 5555);
  });

  test('a daemon error is best-effort → empty map (labels stay generic)',
      () async {
    final adapter = _StubAdapter(const {'detail': 'host is required'},
        status: 400);
    final api = AlpacaDeviceNamesApi(server,
        dio: Dio(BaseOptions(baseUrl: server.baseUrl))
          ..httpClientAdapter = adapter);
    expect(await api.fetchNames('', 6800), isEmpty);
  });

  test('no active server → the no-server stand-in, which answers empty',
      () async {
    final container = ProviderContainer(overrides: [
      activeServerProvider.overrideWithValue(null),
    ]);
    addTearDown(container.dispose);
    final client = container.read(alpacaDeviceNamesApiProvider);
    expect(client, isA<NoServerAlpacaDeviceNames>());
    expect(await client.fetchNames('rig', 6800), isEmpty);
  });

  test('an active server → the daemon-backed client', () {
    final container = ProviderContainer(overrides: [
      activeServerProvider.overrideWithValue(server),
    ]);
    addTearDown(container.dispose);
    expect(container.read(alpacaDeviceNamesApiProvider),
        isA<AlpacaDeviceNamesApi>());
  });

  group('AlpacaDeviceNamesApi.parseNames (#1067 daemon envelope)', () {
    test('maps the names object verbatim', () {
      expect(
        AlpacaDeviceNamesApi.parseNames({
          'names': {'camera/1': 'ZWO ASI290MM Mini', 'telescope/0': 'AM5N'},
        }),
        {'camera/1': 'ZWO ASI290MM Mini', 'telescope/0': 'AM5N'},
      );
    });

    test('anything malformed is an empty map (labels stay generic)', () {
      expect(AlpacaDeviceNamesApi.parseNames(null), isEmpty);
      expect(AlpacaDeviceNamesApi.parseNames('nope'), isEmpty);
      expect(AlpacaDeviceNamesApi.parseNames({'names': []}), isEmpty);
      expect(
        AlpacaDeviceNamesApi.parseNames({
          'names': {'camera/1': '', 'x': 3},
        }),
        isEmpty,
      );
    });
  });
}
