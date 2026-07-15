import 'dart:convert';
import 'dart:io';
import 'dart:typed_data';

import 'package:dio/dio.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/models/server.dart';
import 'package:openastroara/services/dso_catalog_service.dart';
import 'package:openastroara/state/settings/optics_settings_state.dart';
import 'package:openastroara/state/settings/site_settings_state.dart';
import 'package:openastroara/util/tonight_sky_local.dart';

/// Dio whose adapter serves a canned JSON body (or a status error).
Dio _cannedDio({required int status, Object? body}) {
  final dio = Dio(BaseOptions(baseUrl: 'http://test'));
  dio.httpClientAdapter = _CannedAdapter(status, body);
  return dio;
}

class _CannedAdapter implements HttpClientAdapter {
  _CannedAdapter(this.status, this.body);
  final int status;
  final Object? body;

  @override
  Future<ResponseBody> fetch(RequestOptions options, Stream<Uint8List>? _,
          Future<void>? cancelFuture) async =>
      ResponseBody.fromString(jsonEncode(body ?? []), status,
          headers: {Headers.contentTypeHeader: [Headers.jsonContentType]});

  @override
  void close({bool force = false}) {}
}

const _m31Row = {
  'name': 'M31',
  'common_name': 'Andromeda Galaxy',
  'type': 'G',
  'magnitude': 3.4,
  'ra_deg': 10.685,
  'dec_deg': 41.269,
  'maj_ax_arcmin': 178.0,
  'min_ax_arcmin': 63.0,
  'surface_brightness': 22.2,
};

void main() {
  late Directory tmp;
  late DsoCatalogService svc;

  setUp(() async {
    tmp = await Directory.systemTemp.createTemp('dso_catalog_test');
    svc = DsoCatalogService(supportDir: () async => tmp);
  });

  tearDown(() async {
    await tmp.delete(recursive: true);
  });

  const server = AraServer(hostname: 'test', port: 1);

  test('refreshFrom writes the mirror; loadCached round-trips it', () async {
    final fetched = await svc.refreshFrom(server,
        dio: _cannedDio(status: 200, body: [
          _m31Row,
          {'name': 'garbled'}, // malformed row — skipped
        ]));
    expect(fetched, hasLength(1));
    final cached = await svc.loadCached();
    expect(cached, hasLength(1));
    expect(cached.single.name, 'Andromeda Galaxy');
    expect(cached.single.sizeMajArcmin, 178.0);
    expect(cached.single.surfaceBrightness, 22.2);
  });

  test('a 404 (catalog not installed) leaves the existing mirror untouched',
      () async {
    await svc.refreshFrom(server,
        dio: _cannedDio(status: 200, body: [_m31Row]));
    final result =
        await svc.refreshFrom(server, dio: _cannedDio(status: 404));
    expect(result, isNull);
    expect(await svc.loadCached(), hasLength(1));
  });

  test('never fetched → empty mirror', () async {
    expect(await svc.loadCached(), isEmpty);
  });

  test('ranker scores real framing + surface brightness from a mirrored row',
      () {
    final list = computeTonightSkyLocal(
      site: const SiteSettings(
          siteName: 't',
          latitudeDeg: 34,
          longitudeDeg: -84,
          bortleClass: 6,
          defaultHorizonAltitudeDeg: 20,
          twilightDefinition: TwilightDefinition.astronomical),
      optics: const OpticsSettings(
          focalLengthMm: 250,
          reducerFactor: 1.0,
          sensorWidthPx: 6248,
          sensorHeightPx: 4176,
          pixelSizeUm: 3.76,
          apertureMm: 51),
      atUtc: DateTime.utc(2026, 10, 15, 3), // autumn — M31 territory
      catalog: [PlanningDso.fromJson(_m31Row)!],
    );
    expect(list, hasLength(1));
    final m31 = list.single;
    expect(m31.sizeMajArcmin, 178.0);
    final why = m31.scoreReasons!.join(' ');
    expect(why, isNot(contains('size unknown')));
    expect(why, contains('Bortle 6 sky'));
  });
}
