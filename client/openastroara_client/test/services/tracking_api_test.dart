import 'dart:convert';
import 'dart:typed_data';

import 'package:dio/dio.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/models/server.dart';
import 'package:openastroara/models/tracking_calibration.dart';
import 'package:openastroara/services/tracking_api.dart';

/// #1264 — the tracking-calibration client against a stub daemon: the record
/// it parses, 404 → "never measured", other failures rethrown, and the
/// recalibrate POST.
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

TrackingApi _api(_Daemon daemon) => TrackingApi(
  const AraServer(hostname: 'x', port: 80),
  dio: Dio()..httpClientAdapter = daemon,
);

const _record = {
  'ceiling_seconds': 90.0,
  'measured_utc': '2026-10-11T22:14:00Z',
  'polar_align_error_arcmin': 2.5,
  'pixel_scale_arcsec': 2.3,
  'seeing_fwhm_arcsec': 3.1,
  'prior_seconds': 60.0,
  'source': 'measured',
  'rungs': [
    {
      'exposure_seconds': 10,
      'eccentricity': 0.21,
      'fwhm_px': 2.0,
      'stars': 140,
      'usable': true,
      'passed': true,
    },
    {
      'exposure_seconds': 60,
      'eccentricity': 0.44,
      'fwhm_px': 2.3,
      'stars': 130,
      'usable': true,
      'passed': true,
    },
    {
      'exposure_seconds': 120,
      'eccentricity': 0.71,
      'fwhm_px': 3.9,
      'stars': 120,
      'usable': true,
      'passed': false,
    },
    {'exposure_seconds': 240, 'stars': 3, 'usable': false, 'passed': false},
  ],
};

void main() {
  test('reads and parses the stored calibration', () async {
    final daemon = _Daemon(200, _record);
    final cal = await _api(daemon).read();
    expect(daemon.seen!.method, 'GET');
    expect(daemon.seen!.path, '/api/v1/tracking/calibration');
    expect(cal, isNotNull);
    expect(cal!.ceilingSeconds, 90);
    expect(cal.measured, isTrue);
    expect(cal.measuredUtc, DateTime.utc(2026, 10, 11, 22, 14));
    expect(cal.priorSeconds, 60);
    expect(cal.polarAlignErrorArcmin, 2.5);
    expect(cal.rungs, hasLength(4));
    expect(cal.rungs[2].passed, isFalse);
    expect(cal.rungs[3].usable, isFalse);
    expect(cal.rungs[3].eccentricity, isNull);
  });

  test('404 is "never measured", not an error', () async {
    expect(await _api(_Daemon(404, {'detail': 'none'})).read(), isNull);
  });

  test('a transport-level failure is rethrown', () async {
    expect(_api(_Daemon(500, {})).read(), throwsA(isA<DioException>()));
  });

  test('#1264 readPrior parses seconds + source; a daemon without the route answers nothing', () async {
    final daemon = _Daemon(200, {'seconds': 13.8, 'source': "the mount's spec sheet (manufacturer)"});
    final p = await _api(daemon).readPrior();
    expect(daemon.seen!.path, '/api/v1/tracking/prior');
    expect(p.seconds, 13.8);
    expect(p.source, "the mount's spec sheet (manufacturer)");
    final none = await _api(_Daemon(200, {'seconds': null, 'source': null})).readPrior();
    expect(none.seconds, isNull);
    final old = await _api(_Daemon(404, {})).readPrior();
    expect(old.seconds, isNull);
  });

  test('recalibrate posts to the clear route', () async {
    final daemon = _Daemon(204, {});
    await _api(daemon).recalibrate();
    expect(daemon.seen!.method, 'POST');
    expect(daemon.seen!.path, '/api/v1/tracking/recalibrate');
  });

  group('TrackingCalibration.fromJson', () {
    test('rejects a body without the two required fields', () {
      expect(TrackingCalibration.fromJson({'source': 'measured'}), isNull);
      expect(TrackingCalibration.fromJson('nope'), isNull);
      expect(
        TrackingCalibration.fromJson({
          'ceiling_seconds': 1,
          'measured_utc': 'garbage',
        }),
        isNull,
      );
    });

    test('a prior-only record reads as not measured', () {
      final c = TrackingCalibration.fromJson({
        'ceiling_seconds': 45,
        'measured_utc': '2026-10-11T22:14:00Z',
        'source': 'prior',
      });
      expect(c!.measured, isFalse);
      expect(c.rungs, isEmpty);
    });
  });

  test('formatExposureSeconds matches the advisor shape', () {
    expect(formatExposureSeconds(45), '45 s');
    expect(formatExposureSeconds(90), '90 s');
    expect(formatExposureSeconds(150), '2.5 min');
    expect(formatExposureSeconds(600), '10 min');
  });
}
