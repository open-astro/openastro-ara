import 'dart:convert';
import 'dart:typed_data';

import 'package:dio/dio.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/models/alpacabridge_setup.dart';
import 'package:openastroara/models/server.dart';
import 'package:openastroara/services/alpacabridge_setup_api.dart';

class _Stub implements HttpClientAdapter {
  _Stub(this.body, {this.status = 200});
  final Object body;
  final int status;
  RequestOptions? last;
  @override
  void close({bool force = false}) {}
  @override
  Future<ResponseBody> fetch(
    RequestOptions o,
    Stream<Uint8List>? s,
    Future<void>? c,
  ) async {
    last = o;
    return ResponseBody.fromString(
      jsonEncode(body),
      status,
      headers: {
        Headers.contentTypeHeader: [Headers.jsonContentType],
      },
    );
  }
}

AlpacaBridgeSetupApi _api(_Stub stub) {
  const server = AraServer(hostname: 'rig', port: 5555);
  return AlpacaBridgeSetupApi(
    server,
    host: '192.0.2.10',
    port: 32323,
    dio: Dio(BaseOptions(baseUrl: server.baseUrl, validateStatus: (_) => true))
      ..httpClientAdapter = stub,
  );
}

void main() {
  test('catalog dials the daemon route with the bridge host and port, never the bridge', () async {
    final stub = _Stub({'device_types': []});
    await _api(stub).catalog();
    expect(AlpacaBridgeSetupApi.route, '/api/v1/equipment/alpacabridge');
    expect(stub.last!.path, '/api/v1/equipment/alpacabridge/catalog');
    expect(stub.last!.queryParameters, {'host': '192.0.2.10', 'port': 32323});
  });

  test('cameras sends the vendor and reads index, name and serial', () async {
    final stub = _Stub({
      'cameras': [
        {'index': 0, 'name': 'ZWO ASI294MC Pro', 'serial': '1A2B3C4D5E6F7A8B'},
      ],
    });
    final cams = await _api(stub).cameras('zwo');
    expect(stub.last!.path, '/api/v1/equipment/alpacabridge/cameras');
    expect(stub.last!.queryParameters, {
      'host': '192.0.2.10',
      'port': 32323,
      'vendor': 'zwo',
    });
    expect(
      (cams.single.index, cams.single.name, cams.single.serial),
      (0, 'ZWO ASI294MC Pro', '1A2B3C4D5E6F7A8B'),
    );
  });

  test(
    'add posts the flat body and reads 200 as Added, with the warning',
    () async {
      final stub = _Stub({
        'message': 'Device registered successfully.',
        'warning': 'no camera matched',
      });
      final out = await _api(stub).addDevice({
        'vendor': 'zwo',
        'deviceType': 'camera',
        'serialNumber': 'S1',
      });
      expect(stub.last!.method, 'POST');
      expect(stub.last!.path, '/api/v1/equipment/alpacabridge/devices');
      expect(stub.last!.data, {
        'vendor': 'zwo',
        'deviceType': 'camera',
        'serialNumber': 'S1',
      });
      expect(
        out,
        isA<AddAdded>().having(
          (o) => o.warning,
          'warning',
          'no camera matched',
        ),
      );
    },
  );

  test('422 reads as Refused with the bridge\'s detail (the API layer decodes it, ADR 0022 rule 5)', () async {
    final out = await _api(
      _Stub({
        'type': 'https://openastro.net/errors/alpacabridge-refused',
        'detail': "gpioChip '/dev/gpiochip9' is not in the board allowlist",
      }, status: 422),
    ).addDevice({'vendor': 'zwo', 'deviceType': 'switch'});
    expect(
      out,
      isA<AddRefused>().having(
        (o) => o.detail,
        'detail',
        contains('allowlist'),
      ),
    );
  });

  test(
    '502 reads as Unconfirmed, and a thrown transport error does too',
    () async {
      final out = await _api(
        _Stub({
          'type': 'https://openastro.net/errors/alpacabridge-unconfirmed',
          'detail': 'did not answer',
        }, status: 502),
      ).addDevice({'vendor': 'zwo', 'deviceType': 'camera'});
      expect(out, isA<AddUnconfirmed>());
    },
  );
}
