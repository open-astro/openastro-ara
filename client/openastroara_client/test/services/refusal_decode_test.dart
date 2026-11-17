import 'dart:convert';
import 'dart:typed_data';

import 'package:dio/dio.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/models/server.dart';
import 'package:openastroara/services/center_api.dart';
import 'package:openastroara/services/plate_solve_api.dart';
import 'package:openastroara/services/refusal.dart';

/// ADR 0022 — the app decodes a Refusal once, in its API layer: a 4xx problem
/// body becomes a typed [Refusal] (cause + parameters). An unknown `reason`
/// or none keeps the daemon's `detail`, so an endpoint not yet moved reads as
/// it does today.
class _Daemon implements HttpClientAdapter {
  _Daemon(this.status, this.body);
  final int status;
  final Object body;

  @override
  void close({bool force = false}) {}

  @override
  Future<ResponseBody> fetch(
    RequestOptions options,
    Stream<Uint8List>? requestStream,
    Future<void>? cancelFuture,
  ) async => ResponseBody.fromString(
    jsonEncode(body),
    status,
    headers: {
      Headers.contentTypeHeader: ['application/problem+json'],
    },
  );
}

const _server = AraServer(hostname: 'x', port: 80);

PlateSolveApi _solver(_Daemon d) =>
    PlateSolveApi(_server, dio: Dio()..httpClientAdapter = d);

Matcher _refusal(
  RefusalCause? cause, {
  String? reason,
  String? detail,
  Map<String, Object?> parameters = const {},
}) => isA<Refusal>()
    .having((r) => r.cause, 'cause', cause)
    .having((r) => r.reason, 'reason', reason ?? cause?.token)
    .having((r) => r.detail, 'detail', detail)
    .having((r) => r.parameters, 'parameters', parameters);

void main() {
  test(
    'Check refused because the camera is busy decodes to the camera-busy cause',
    () async {
      final d = _Daemon(409, {
        'title': 'The camera is busy',
        'status': 409,
        'detail': 'The camera is busy with an exposure.',
        'reason': 'camera-busy',
      });
      await expectLater(
        _solver(d).verifyPosition(),
        throwsA(
          _refusal(
            RefusalCause.cameraBusy,
            detail: 'The camera is busy with an exposure.',
          ),
        ),
      );
    },
  );

  test(
    'a solve refused for unset optics decodes the cause and the missing values',
    () async {
      final d = _Daemon(422, {
        'title': 'Optics not set',
        'status': 422,
        'detail': 'Cannot plate-solve: telescope focal length (NaN) must be configured.',
        'reason': 'optics-not-set',
        'missing': ['focal_length'],
      });
      await expectLater(
        _solver(d).solve('frame-1'),
        throwsA(
          _refusal(
            RefusalCause.opticsNotSet,
            detail: 'Cannot plate-solve: telescope focal length (NaN) must be configured.',
            parameters: {
              'missing': ['focal_length'],
            },
          ),
        ),
      );
    },
  );

  test('Center refused while another centring runs decodes the cause and the running job', () async {
    final d = _Daemon(409, {
      'title': 'Another centring is running',
      'status': 409,
      'detail': 'A centering job for another target is already running.',
      'reason': 'center-in-progress',
      'job_id': '0b6c0e8e-4c1f-4d43-9b9a-2f7d1c4e5a10',
    });
    final api = CenterApi(_server, dio: Dio()..httpClientAdapter = d);
    await expectLater(
      api.start(5.5, 20),
      throwsA(
        _refusal(
          RefusalCause.centerInProgress,
          detail: 'A centering job for another target is already running.',
          parameters: {'job_id': '0b6c0e8e-4c1f-4d43-9b9a-2f7d1c4e5a10'},
        ),
      ),
    );
  });

  test(
    'a Center that fails on the daemon stays a server error, not a Refusal',
    () async {
      final d = _Daemon(500, {
        'title': 'Internal Server Error',
        'status': 500,
        'detail': 'boom',
      });
      final api = CenterApi(_server, dio: Dio()..httpClientAdapter = d);
      await expectLater(api.start(5.5, 20), throwsA(isA<DioException>()));
    },
  );

  test('a reason this app does not know keeps the daemon detail', () async {
    final d = _Daemon(409, {
      'title': 'Refused',
      'status': 409,
      'detail': 'The dome is closed.',
      'reason': 'dome-closed',
    });
    await expectLater(
      _solver(d).verifyPosition(),
      throwsA(
        _refusal(null, reason: 'dome-closed', detail: 'The dome is closed.'),
      ),
    );
  });

  test('an answer with no reason keeps the daemon detail', () async {
    final d = _Daemon(422, {
      'title': 'Unprocessable Entity',
      'status': 422,
      'detail': 'Plate-solver executable not found.',
    });
    await expectLater(
      _solver(d).verifyPosition(),
      throwsA(_refusal(null, detail: 'Plate-solver executable not found.')),
    );
  });
}
