import 'dart:convert';
import 'dart:typed_data';

import 'package:dio/dio.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/models/server.dart';
import 'package:openastroara/services/plate_solve_api.dart';

/// §18.I / #1150 — the plate-solve client against a stub daemon: the route it
/// posts to, the solution it parses, and the two readable failures (404 frame
/// missing, 422 misconfiguration with the daemon's own detail) versus a plain
/// transport error that is rethrown.
class _Daemon implements HttpClientAdapter {
  _Daemon(this.status, this.body);
  final int status;
  final Object body;
  RequestOptions? seen;

  @override
  void close({bool force = false}) {}

  @override
  Future<ResponseBody> fetch(
    RequestOptions options,
    Stream<Uint8List>? requestStream,
    Future<void>? cancelFuture,
  ) async {
    seen = options;
    return ResponseBody.fromString(
      jsonEncode(body),
      status,
      headers: {
        Headers.contentTypeHeader: [Headers.jsonContentType],
      },
    );
  }
}

PlateSolveApi _api(_Daemon daemon) => PlateSolveApi(
  const AraServer(hostname: 'x', port: 80),
  dio: Dio()..httpClientAdapter = daemon,
);

void main() {
  test('posts to the frame solve route and parses the solution', () async {
    final daemon = _Daemon(200, {
      'success': true,
      'ra': 83.82,
      'dec': -5.39,
      'orientation': 12.5,
      'pixel_scale': 3.1,
    });
    final result = await _api(daemon).solve('frame-1');
    expect(daemon.seen!.method, 'POST');
    expect(daemon.seen!.path, '/api/v1/platesolve/frames/frame-1/solve');
    expect(result.success, isTrue);
    expect(result.ra, closeTo(83.82, 1e-9));
    expect(result.dec, closeTo(-5.39, 1e-9));
  });

  test('a 404 becomes a readable "capture a frame first" failure', () async {
    final daemon = _Daemon(404, {'title': 'Not Found'});
    expect(
      () => _api(daemon).solve('gone'),
      throwsA(
        isA<PlateSolveException>().having(
          (e) => e.message,
          'message',
          contains('Capture a frame first'),
        ),
      ),
    );
  });

  test('a 422 surfaces the daemon\'s own detail', () async {
    final daemon = _Daemon(422, {
      'type': 'https://openastro.net/errors/plate-solver-not-configured',
      'title': 'Plate solver not configured',
      'detail': 'Cannot plate-solve: telescope focal length (NaN) must be configured.',
    });
    expect(
      () => _api(daemon).solve('f'),
      throwsA(
        isA<PlateSolveException>().having(
          (e) => e.message,
          'message',
          contains('focal length'),
        ),
      ),
    );
  });

  test('a 422 without detail falls back to the settings hint', () async {
    final daemon = _Daemon(422, <String, dynamic>{});
    expect(
      () => _api(daemon).solve('f'),
      throwsA(
        isA<PlateSolveException>().having(
          (e) => e.message,
          'message',
          contains('ASTAP path'),
        ),
      ),
    );
  });

  test('any other failure is rethrown as the transport error', () async {
    final daemon = _Daemon(500, {'title': 'boom'});
    expect(() => _api(daemon).solve('f'), throwsA(isA<DioException>()));
  });
}
