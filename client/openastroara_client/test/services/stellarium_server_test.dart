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
    test('serves DSS2 JPEG tiles as image/jpeg', () {
      expect(StellariumServer.contentTypeFor('/dss/Norder3/Dir0/Npix0.jpg').mimeType,
          'image/jpeg');
    });
    test('serves the DSS2 properties manifest as text', () {
      expect(StellariumServer.contentTypeFor('/dss/properties').mimeType, 'text/plain');
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

  group('StellariumServer.dssRelativePath', () {
    test('accepts the HiPS manifest, Allsky and tile paths the engine requests', () {
      expect(StellariumServer.dssRelativePath('/dss/properties'), 'properties');
      expect(StellariumServer.dssRelativePath('/dss/Norder3/Allsky.jpg'),
          'Norder3/Allsky.jpg');
      expect(StellariumServer.dssRelativePath('/dss/Norder7/Dir10000/Npix12345.jpg'),
          'Norder7/Dir10000/Npix12345.jpg');
    });
    test('refuses empty and dot segments and anything outside the prefix', () {
      // The engine joins `url + "/" + path` verbatim, so a data-source URL
      // with a trailing slash produced exactly this — and DSS never loaded.
      expect(StellariumServer.dssRelativePath('/dss//properties'), isNull);
      expect(StellariumServer.dssRelativePath('/dss/'), isNull);
      expect(StellariumServer.dssRelativePath('/dss'), isNull);
      expect(StellariumServer.dssRelativePath('/dss/../index.html'), isNull);
      expect(StellariumServer.dssRelativePath('/dss/Norder3/./Allsky.jpg'), isNull);
      expect(StellariumServer.dssRelativePath('/dss/a%2f..%2fb'), isNull);
      expect(StellariumServer.dssRelativePath('/skydata/stars'), isNull);
    });
  });

  // Cache HITS and refused paths never leave the machine, so they too can be
  // black-box tested over loopback. (A miss would fetch from CDS — not here.)
  group('StellariumServer /dss', () {
    late StellariumServer server;
    setUpAll(() async {
      server = await StellariumServer.start();
    });
    tearDownAll(() async => server.dispose());

    Future<HttpClientResponse> send(String method, String path) async {
      final client = HttpClient();
      try {
        final req = await client.openUrl(
            method, Uri.parse('${server.baseUrl}$path'));
        return await req.close();
      } finally {
        client.close(force: true);
      }
    }

    test("refuses the double-slash path a trailing-slash data source produces",
        () async {
      expect((await send('GET', '/dss//properties')).statusCode,
          HttpStatus.forbidden);
      expect((await send('GET', '/dss/')).statusCode, HttpStatus.forbidden);
      // (`..` is covered by the dssRelativePath unit test above — Dart's
      // HttpClient normalises dot segments away before the request is sent.)
    });

    test('serves a cached tile from disk (GET body, HEAD length only)',
        () async {
      final tile = File('${server.dssCacheDir.path}/Norder3/Dir0/Npix1.jpg');
      await tile.parent.create(recursive: true);
      await tile.writeAsBytes([0xFF, 0xD8, 0xFF, 0xD9]);
      try {
        final get = await send('GET', '/dss/Norder3/Dir0/Npix1.jpg');
        expect(get.statusCode, HttpStatus.ok);
        expect(get.headers.contentType?.mimeType, 'image/jpeg');
        expect(await get.fold<List<int>>([], (a, b) => a..addAll(b)),
            [0xFF, 0xD8, 0xFF, 0xD9]);
        final head = await send('HEAD', '/dss/Norder3/Dir0/Npix1.jpg');
        expect(head.statusCode, HttpStatus.ok);
        expect(head.contentLength, 4);
        expect(await head.fold<int>(0, (n, b) => n + b.length), 0);
      } finally {
        await tile.delete();
      }
    });

    test('rejects methods other than GET/HEAD', () async {
      expect((await send('POST', '/dss/properties')).statusCode,
          HttpStatus.methodNotAllowed);
    });
  });

  group('planetarium page DSS data source', () {
    test('points at the loopback cache WITHOUT a trailing slash', () {
      // hips.c get_url_for() emits `<url>/<path>`; './dss/' would request
      // '/dss//properties', which dssRelativePath rightly refuses.
      final page = File('assets/stellarium/index.html').readAsStringSync();
      expect(page, contains("core.dss.addDataSource({ url: './dss' })"));
      expect(page, isNot(contains("url: './dss/'")));
    });
  });
}
