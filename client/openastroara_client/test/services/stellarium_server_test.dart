import 'dart:convert';
import 'dart:io';

import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/services/stellarium_server.dart';

// Note: the live loopback-serving path (start() → HTTP GET of bundled engine +
// sky data) is exercised on-device, not here — `flutter test`'s rootBundle does
// not reliably serve real binary asset bytes, so a self-hosted asset server can't
// be black-box tested in the unit harness. These cover the content-type logic,
// which is the part with real branching.
void main() {
  group('StellariumServer.contentTypeFor', () {
    test('serves WASM with the correct type (needed for streaming instantiation)', () {
      expect(StellariumServer.contentTypeFor('/stellarium-web-engine.wasm').toString(),
          'application/wasm');
    });
    test('serves the bridge page as HTML and the engine as JavaScript', () {
      expect(StellariumServer.contentTypeFor('/index.html').mimeType, 'text/html');
      expect(StellariumServer.contentTypeFor('/stellarium-web-engine.js').mimeType,
          'text/javascript');
    });
    test('serves gzipped data as gzip (the engine inflates it itself)', () {
      expect(StellariumServer.contentTypeFor('/skydata/tle_satellite.jsonl.gz').mimeType,
          'application/gzip');
    });
    test('serves .webp landscape/art tiles as image/webp', () {
      expect(StellariumServer.contentTypeFor('/skydata/landscapes/guereins/tile.webp').mimeType,
          'image/webp');
    });
    test('unknown / binary sky-data blobs fall back to octet-stream', () {
      expect(StellariumServer.contentTypeFor('/skydata/dso/Norder0/Dir0/Npix0.eph').mimeType,
          'application/octet-stream');
    });
  });

  group('StellariumServer.parseRange', () {
    test('parses a closed range', () {
      expect(StellariumServer.parseRange('bytes=10-20', 100), (10, 20));
    });
    test('open-ended range runs to the last byte', () {
      expect(StellariumServer.parseRange('bytes=10-', 100), (10, 99));
    });
    test('suffix range returns the last N bytes', () {
      expect(StellariumServer.parseRange('bytes=-15', 100), (85, 99));
    });
    test('rejects a malformed "bytes=-1-10" rather than throwing', () {
      // Leading dash → the suffix branch with a non-numeric "1-10" → null (never
      // reaches sublist with a bad index).
      expect(StellariumServer.parseRange('bytes=-1-10', 100), isNull);
    });
    test('rejects a start at/after the end of the resource', () {
      expect(StellariumServer.parseRange('bytes=100-', 100), isNull);
    });
    test('rejects an unsatisfiable range (last-pos < first-pos) instead of a 1-byte slice', () {
      expect(StellariumServer.parseRange('bytes=50-10', 100), isNull);
    });
    test('returns null for a non-bytes or malformed header', () {
      expect(StellariumServer.parseRange('items=0-1', 100), isNull);
      expect(StellariumServer.parseRange(null, 100), isNull);
    });
  });

  // The /aracat routes never touch rootBundle (they answer from the two
  // static resolvers), so unlike the asset path they CAN be black-box tested
  // through a real loopback GET. Plain test() on purpose: the widget-test
  // binding swaps HttpClient for a mock that 400s everything.
  group('StellariumServer /aracat', () {
    late StellariumServer server;
    setUpAll(() async {
      server = await StellariumServer.start();
    });
    tearDownAll(() async {
      StellariumServer.catalogListResolver = null;
      StellariumServer.catalogObjectsResolver = null;
      await server.dispose();
    });
    setUp(() {
      StellariumServer.catalogListResolver = () async => [
            {'id': 'messier', 'count': 110},
          ];
      StellariumServer.catalogObjectsResolver = (id, limit) async => [
            {'id': id, 'limit': limit},
          ];
    });

    Future<({int status, String? type, String body})> get(String path,
        {bool withToken = true}) async {
      final client = HttpClient();
      try {
        final req = await client.getUrl(Uri.parse('${server.baseUrl}$path'));
        if (withToken) req.headers.set('x-ara-token', server.token);
        final res = await req.close();
        return (
          status: res.statusCode,
          type: res.headers.contentType?.mimeType,
          body: await utf8.decodeStream(res),
        );
      } finally {
        client.close(force: true);
      }
    }

    test('refuses a request without the per-run token', () async {
      expect((await get('/aracat', withToken: false)).status,
          HttpStatus.forbidden);
      expect((await get('/aracat/messier', withToken: false)).status,
          HttpStatus.forbidden);
    });

    test('lists the catalogs as JSON from the list resolver', () async {
      final r = await get('/aracat');
      expect(r.status, HttpStatus.ok);
      expect(r.type, 'application/json');
      expect(jsonDecode(r.body), [
        {'id': 'messier', 'count': 110},
      ]);
    });

    test('decodes the catalog id and parses ?limit=, defaulting to 500',
        () async {
      expect(jsonDecode((await get('/aracat/wr-stars?limit=25')).body), [
        {'id': 'wr-stars', 'limit': 25},
      ]);
      expect(jsonDecode((await get('/aracat/sh2')).body), [
        {'id': 'sh2', 'limit': 500},
      ]);
      expect(jsonDecode((await get('/aracat/sh2?limit=lots')).body), [
        {'id': 'sh2', 'limit': 500},
      ]);
      expect(jsonDecode((await get('/aracat/Sharpless%202')).body), [
        {'id': 'Sharpless 2', 'limit': 500},
      ]);
    });

    test('404s an unknown catalog and an unwired resolver', () async {
      StellariumServer.catalogObjectsResolver = (id, limit) async => null;
      expect((await get('/aracat/nope')).status, HttpStatus.notFound);
      StellariumServer.catalogListResolver = null;
      expect((await get('/aracat')).status, HttpStatus.notFound);
    });
  });
}
