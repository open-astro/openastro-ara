import 'dart:io';
import 'dart:typed_data';

import 'package:dio/dio.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/services/target_preview_service.dart';

/// Dio whose adapter serves canned bytes (or fails), counting requests.
class _Adapter implements HttpClientAdapter {
  _Adapter({this.status = 200, this.bytes = const [1, 2, 3], this.throwIt = false});
  final int status;
  final List<int> bytes;
  final bool throwIt;
  int calls = 0;
  Uri? lastUri;

  @override
  Future<ResponseBody> fetch(RequestOptions options, Stream<Uint8List>? _,
      Future<void>? cancelFuture) async {
    calls++;
    lastUri = options.uri;
    if (throwIt) throw const SocketException('offline');
    return ResponseBody.fromBytes(Uint8List.fromList(bytes), status);
  }

  @override
  void close({bool force = false}) {}
}

void main() {
  late Directory tmp;
  setUp(() async {
    tmp = await Directory.systemTemp.createTemp('preview-');
  });
  tearDown(() => tmp.delete(recursive: true));

  TargetPreviewService svc(_Adapter a) => TargetPreviewService(
      supportDir: () async => tmp,
      dio: Dio(BaseOptions(responseType: ResponseType.bytes))
        ..httpClientAdapter = a);

  test('field of view scales with the object and stays in bounds', () {
    expect(TargetPreviewService.fovDegFor(null), 0.4);
    expect(TargetPreviewService.fovDegFor(5), 0.4); // tiny galaxy: floor
    expect(TargetPreviewService.fovDegFor(60), closeTo(2.5, 1e-9));
    expect(TargetPreviewService.fovDegFor(600), 6.0); // Sh2 field: cap
  });

  test('cutout URL asks hips2fits for a DSS2 colour JPEG at the target', () {
    final u = TargetPreviewService.cutoutUri(raDeg: 307.0, decDeg: 44.9, fovDeg: 1.5);
    expect(u.host, 'alasky.u-strasbg.fr');
    expect(u.queryParameters['hips'], 'CDS/P/DSS2/color');
    expect(u.queryParameters['ra'], '307.00000');
    expect(u.queryParameters['dec'], '44.90000');
    expect(u.queryParameters['fov'], '1.500');
    expect(u.queryParameters['format'], 'jpg');
  });

  test('fetches once, caches to disk, then serves the cache without network',
      () async {
    final a = _Adapter(bytes: [9, 8, 7]);
    final s = svc(a);
    final first = await s.load(id: 'Sh2-110', raDeg: 307, decDeg: 44.9, sizeMajArcmin: 30);
    expect(first, [9, 8, 7]);
    expect(a.calls, 1);
    expect(File('${tmp.path}/target_previews/${TargetPreviewService.cacheName('Sh2-110', 1.25)}').existsSync(), isTrue);

    final again = await s.load(id: 'Sh2-110', raDeg: 307, decDeg: 44.9, sizeMajArcmin: 30);
    expect(again, [9, 8, 7]);
    expect(a.calls, 1, reason: 'cache hit — no second request');

    // A fresh service (new session) over the same dir is still a cache hit.
    final offline = svc(_Adapter(throwIt: true));
    expect(await offline.load(id: 'Sh2-110', raDeg: 307, decDeg: 44.9, sizeMajArcmin: 30), [9, 8, 7]);
  });

  test('offline with nothing cached degrades to null, never throws', () async {
    final s = svc(_Adapter(throwIt: true));
    expect(await s.load(id: 'LDN 1235', raDeg: 1, decDeg: 2), isNull);
    expect(await s.cached('LDN 1235'), isNull);
  });

  test('a non-200 or empty body is not cached', () async {
    final s = svc(_Adapter(status: 500));
    expect(await s.load(id: 'M31', raDeg: 10.7, decDeg: 41.3, sizeMajArcmin: 178), isNull);
    expect(await s.cached('M31', sizeMajArcmin: 178), isNull);
    final empty = svc(_Adapter(bytes: const []));
    expect(await empty.load(id: 'M31', raDeg: 10.7, decDeg: 41.3, sizeMajArcmin: 178), isNull);
  });

  test('cache names are filename-safe and keyed by framing', () {
    expect(TargetPreviewService.cacheName('LDN 1235', 6.0), 'LDN_1235-6.00.jpg');
    expect(TargetPreviewService.cacheName('a/b:c', 0.4), 'a_b_c-0.40.jpg');
  });
}
