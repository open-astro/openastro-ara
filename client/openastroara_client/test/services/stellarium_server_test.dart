import 'dart:convert';
import 'dart:io';
import 'dart:typed_data';

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
      // The same rule now types the bundled skydata manifests (previously
      // application/octet-stream); the engine reads them as bytes either way.
      expect(StellariumServer.contentTypeFor('/skydata/stars/properties').mimeType, 'text/plain');
    });
    test('unknown / binary sky-data blobs fall back to octet-stream', () {
      expect(StellariumServer.contentTypeFor('/skydata/dso/Norder0/Dir0/Npix0.eph').mimeType,
          'application/octet-stream');
    });
  });

  group('StellariumServer.isDssMediaType', () {
    test('tiles must be images, the manifest must be non-HTML text', () {
      expect(StellariumServer.isDssMediaType('Norder3/Dir0/Npix1.jpg', ContentType('image', 'jpeg')), isTrue);
      expect(StellariumServer.isDssMediaType('Norder3/Allsky.jpg', ContentType('image', 'png')), isTrue);
      expect(StellariumServer.isDssMediaType('properties', ContentType('text', 'plain', charset: 'utf-8')), isTrue);
      // What a captive portal serves for either.
      expect(StellariumServer.isDssMediaType('Norder3/Dir0/Npix1.jpg', ContentType.html), isFalse);
      expect(StellariumServer.isDssMediaType('properties', ContentType.html), isFalse);
      expect(StellariumServer.isDssMediaType('Norder3/Dir0/Npix1.jpg', ContentType.json), isFalse);
      expect(StellariumServer.isDssMediaType('Norder3/Dir0/Npix1.jpg', null), isFalse);
      expect(StellariumServer.isDssMediaType('properties', ContentType('image', 'jpeg')), isFalse);
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
    final savedOrigin = StellariumServer.dssOrigin;
    setUpAll(() async {
      // A miss in this group must not reach the real CDS: point upstream at a
      // port nothing listens on, so a fetch fails fast and deterministically.
      final dead = await ServerSocket.bind(InternetAddress.loopbackIPv4, 0);
      final deadPort = dead.port;
      await dead.close();
      StellariumServer.dssOrigin = Uri.parse('http://127.0.0.1:$deadPort/');
      server = await StellariumServer.start();
    });
    tearDownAll(() async {
      StellariumServer.dssOrigin = savedOrigin;
      await server.dispose();
    });

    // Read the body BEFORE the client is closed: a force-close in `finally`
    // with the body still in flight is a race the Windows runner loses
    // ("Connection closed while receiving data"). A HEAD has no body to read.
    Future<({int status, int length, String? type, List<int> body})> send(
        String method, String path) async {
      final client = HttpClient();
      try {
        final req = await client.openUrl(
            method, Uri.parse('${server.baseUrl}$path'));
        final res = await req.close();
        final body = method == 'HEAD'
            ? const <int>[]
            : await res.fold<List<int>>([], (a, b) => a..addAll(b));
        return (
          status: res.statusCode,
          length: res.contentLength,
          type: res.headers.contentType?.mimeType,
          body: body,
        );
      } finally {
        client.close(force: true);
      }
    }

    test("refuses the double-slash path a trailing-slash data source produces",
        () async {
      expect((await send('GET', '/dss//properties')).status,
          HttpStatus.forbidden);
      expect((await send('GET', '/dss/')).status, HttpStatus.forbidden);
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
        expect(get.status, HttpStatus.ok);
        expect(get.type, 'image/jpeg');
        expect(get.body, [0xFF, 0xD8, 0xFF, 0xD9]);
        final head = await send('HEAD', '/dss/Norder3/Dir0/Npix1.jpg');
        expect(head.status, HttpStatus.ok);
        expect(head.type, 'image/jpeg');
        expect(head.length, 4);
      } finally {
        await tile.delete();
      }
    });

    test('rejects methods other than GET/HEAD', () async {
      expect((await send('POST', '/dss/properties')).status,
          HttpStatus.methodNotAllowed);
    });

    // The DNS-rebind guard: a page at http://evil.example resolving to
    // 127.0.0.1 still sends its own hostname in Host. Connect to the loopback
    // address and forge the header, so no name resolution is involved.
    test('refuses a Host that is not our own loopback origin', () async {
      final port = Uri.parse(server.baseUrl).port;
      Future<int> withHost(String host) async {
        final client = HttpClient();
        try {
          final req = await client.getUrl(Uri.parse('${server.baseUrl}/dss/properties'));
          req.headers.set(HttpHeaders.hostHeader, host);
          final res = await req.close();
          await res.drain<void>();
          return res.statusCode;
        } finally {
          client.close(force: true);
        }
      }
      expect(await withHost('localhost:$port'), HttpStatus.forbidden);
      expect(await withHost('evil.example:$port'), HttpStatus.forbidden);
      expect(await withHost('127.0.0.1:1'), HttpStatus.forbidden);
      // Sanity: our own origin is let through to the cache lookup; the miss
      // against the dead upstream is a 404, never a 403.
      expect(await withHost('127.0.0.1:$port'), HttpStatus.notFound);
    });
  });

  group('StellariumServer dispose', () {
    test('removes the temp-fallback tile cache it created', () async {
      // Under `flutter test` the path provider is not available, so start()
      // falls back to a private mkdtemp directory; it must not outlive the
      // server (one per run/test group would pile up in the system temp).
      final server = await StellariumServer.start();
      final dir = server.dssCacheDir;
      expect(dir.existsSync(), isTrue);
      expect(dir.path, contains('openastroara-dss2-'));
      await server.dispose();
      expect(dir.existsSync(), isFalse);
    });
  });

  // The download/persist half against a local stub origin: what lands on
  // disk, what is refused, and what the page's /dss/status probe reports.
  group('StellariumServer /dss fetch (stub origin)', () {
    late HttpServer origin;
    late StellariumServer server;
    var originHits = 0;
    final tile = Uint8List.fromList([0xFF, 0xD8, 1, 2, 3, 0xFF, 0xD9]);
    final savedOrigin = StellariumServer.dssOrigin;
    final savedCap = StellariumServer.maxDssResourceBytes;
    final savedBodyTimeout = StellariumServer.dssBodyTimeout;
    final stalled = <HttpResponse>[];

    setUpAll(() async {
      origin = await HttpServer.bind(InternetAddress.loopbackIPv4, 0);
      origin.listen((req) async {
        originHits++;
        final path = req.uri.path;
        if (path == '/Norder3/Dir0/Npix7.jpg') {
          req.response.headers.contentType = ContentType('image', 'jpeg');
          req.response.add(tile);
        } else if (path == '/Norder3/Dir0/Npix8.jpg') {
          // Over the test cap. Typed as an image so the media-type gate lets
          // the body reach _readCapped; untyped, the gate refuses it first and
          // the cap is never exercised.
          req.response.headers.contentType = ContentType('image', 'jpeg');
          req.response.add(List<int>.filled(64, 7));
        } else if (path == '/Norder3/Dir0/Npix12.jpg') {
          // 404 headers, then the link drops: the body never comes and the
          // connection is never closed (half-open TCP after a hotspot switch).
          req.response.statusCode = HttpStatus.notFound;
          req.response.contentLength = 10;
          req.response.add([0]); // one byte forces the headers onto the wire
          await req.response.flush();
          stalled.add(req.response);
          return;
        } else {
          req.response.statusCode = HttpStatus.notFound;
        }
        await req.response.close();
      });
      StellariumServer.dssOrigin = Uri.parse('http://127.0.0.1:${origin.port}/');
      StellariumServer.maxDssResourceBytes = 32;
      StellariumServer.dssBodyTimeout = const Duration(seconds: 1);
      server = await StellariumServer.start();
    });
    tearDownAll(() async {
      StellariumServer.dssOrigin = savedOrigin;
      StellariumServer.maxDssResourceBytes = savedCap;
      StellariumServer.dssBodyTimeout = savedBodyTimeout;
      await server.dispose();
      for (final r in stalled) {
        try {
          await r.close();
        } catch (_) {/* peer already gone */}
      }
      await origin.close(force: true);
    });
    // Each test starts with no backoff armed and no offline flag: a refusal
    // in one test must not let the next one pass without contacting the origin.
    setUp(() => server.resetDssState());

    Future<({int status, List<int> body})> get(String path) async {
      final client = HttpClient();
      try {
        final res = await (await client.getUrl(Uri.parse('${server.baseUrl}$path'))).close();
        return (status: res.statusCode, body: await res.fold<List<int>>([], (a, b) => a..addAll(b)));
      } finally {
        client.close(force: true);
      }
    }

    Future<Map<String, Object?>> status() async =>
        jsonDecode(utf8.decode((await get('/dss/status')).body)) as Map<String, Object?>;

    test('a miss downloads once, persists atomically, then serves from disk', () async {
      final before = originHits;
      final first = await get('/dss/Norder3/Dir0/Npix7.jpg');
      expect(first.status, HttpStatus.ok);
      expect(first.body, tile);
      final file = File('${server.dssCacheDir.path}/Norder3/Dir0/Npix7.jpg');
      expect(await file.readAsBytes(), tile);
      expect(file.parent.listSync().where((e) => e.path.contains('.part-')), isEmpty);
      final second = await get('/dss/Norder3/Dir0/Npix7.jpg');
      expect(second.body, tile);
      expect(originHits - before, 1, reason: 'second request must be a cache hit');
      expect((await status())['offline'], false);
    });

    test("an upstream 404 is the survey's answer: 404 through, nothing written, still online",
        () async {
      expect((await get('/dss/Norder3/Dir0/Npix9.jpg')).status, HttpStatus.notFound);
      expect(File('${server.dssCacheDir.path}/Norder3/Dir0/Npix9.jpg').existsSync(), isFalse);
      expect((await status())['offline'], false);
    });

    test('a 404 whose body never arrives is bounded like every other read',
        () async {
      // The coalesced fetch must complete (404 to the page, entry released)
      // within the body deadline, not hang for the app's life.
      final res = await get('/dss/Norder3/Dir0/Npix12.jpg')
          .timeout(const Duration(seconds: 4));
      expect(res.status, HttpStatus.notFound);
    });

    test('a body over the cap is refused and not persisted', () async {
      expect((await get('/dss/Norder3/Dir0/Npix8.jpg')).status, HttpStatus.notFound);
      expect(File('${server.dssCacheDir.path}/Norder3/Dir0/Npix8.jpg').existsSync(), isFalse);
    });

    test('an unreachable origin flips /dss/status to offline and arms the backoff', () async {
      final dead = await ServerSocket.bind(InternetAddress.loopbackIPv4, 0);
      final deadPort = dead.port;
      await dead.close(); // nothing listens here now → connection refused
      StellariumServer.dssOrigin = Uri.parse('http://127.0.0.1:$deadPort/');
      try {
        expect((await status())['offline'], false, reason: 'fresh state per test');
        expect((await get('/dss/Norder4/Dir0/Npix1.jpg')).status, HttpStatus.notFound);
        expect((await status())['offline'], true);
        // Within the backoff window a further miss is answered from the cache
        // state alone — the (now restored) origin is not contacted.
        StellariumServer.dssOrigin = Uri.parse('http://127.0.0.1:${origin.port}/');
        final before = originHits;
        expect((await get('/dss/Norder4/Dir0/Npix2.jpg')).status, HttpStatus.notFound);
        expect(originHits, before);
        // Cache hits still serve during the backoff.
        expect((await get('/dss/Norder3/Dir0/Npix7.jpg')).status, HttpStatus.ok);
      } finally {
        StellariumServer.dssOrigin = Uri.parse('http://127.0.0.1:${origin.port}/');
      }
    });
  });

  // A captive portal (hotel / campground / airport Wi-Fi) answers every URL
  // with its login page: a 302 to the portal, or a bare 200 of HTML. Neither
  // is a tile; persisting one would serve HTML as image/jpeg, immutable, for
  // ever (nothing evicts the cache). Own group: a portal arms the backoff.
  group('StellariumServer /dss fetch (captive portal)', () {
    late HttpServer origin;
    late StellariumServer server;
    final savedOrigin = StellariumServer.dssOrigin;
    const portalHtml = '<html><body>Please log in</body></html>';

    setUpAll(() async {
      origin = await HttpServer.bind(InternetAddress.loopbackIPv4, 0);
      origin.listen((req) async {
        final path = req.uri.path;
        if (path == '/portal') {
          req.response.headers.contentType = ContentType.html;
          req.response.write(portalHtml);
        } else if (path == '/Norder3/Dir0/Npix20.jpg') {
          // Portal that redirects.
          req.response.statusCode = HttpStatus.found;
          req.response.headers.set(HttpHeaders.locationHeader, '/portal');
        } else if (path == '/Norder3/Dir0/Npix21.jpg' || path == '/properties') {
          // Portal that rewrites the body in place (200 + HTML).
          req.response.headers.contentType = ContentType.html;
          req.response.write(portalHtml);
        } else {
          req.response.statusCode = HttpStatus.notFound;
        }
        await req.response.close();
      });
      StellariumServer.dssOrigin = Uri.parse('http://127.0.0.1:${origin.port}/');
      server = await StellariumServer.start();
    });
    tearDownAll(() async {
      StellariumServer.dssOrigin = savedOrigin;
      await server.dispose();
      await origin.close(force: true);
    });
    setUp(() => server.resetDssState());

    Future<({int status, List<int> body})> get(String path) async {
      final client = HttpClient();
      try {
        final res = await (await client.getUrl(Uri.parse('${server.baseUrl}$path'))).close();
        return (status: res.statusCode, body: await res.fold<List<int>>([], (a, b) => a..addAll(b)));
      } finally {
        client.close(force: true);
      }
    }

    Future<Map<String, Object?>> status() async =>
        jsonDecode(utf8.decode((await get('/dss/status')).body)) as Map<String, Object?>;

    test('a redirected tile is refused, not persisted, and reads as offline', () async {
      expect((await get('/dss/Norder3/Dir0/Npix20.jpg')).status, HttpStatus.notFound);
      expect(File('${server.dssCacheDir.path}/Norder3/Dir0/Npix20.jpg').existsSync(), isFalse);
      expect((await status())['offline'], true);
    });

    test('a 200 whose body is not an image is refused and not persisted', () async {
      expect((await status())['offline'], false, reason: 'fresh state per test');
      expect((await get('/dss/Norder3/Dir0/Npix21.jpg')).status, HttpStatus.notFound);
      expect(File('${server.dssCacheDir.path}/Norder3/Dir0/Npix21.jpg').existsSync(), isFalse);
      expect((await status())['offline'], true);
      server.resetDssState();
      expect((await get('/dss/properties')).status, HttpStatus.notFound);
      expect(File('${server.dssCacheDir.path}/properties').existsSync(), isFalse);
      expect((await status())['offline'], true);
    });
  });

  group('planetarium page site poll', () {
    test('polls the daemon site slowly; Dart pushes this client\'s edits', () {
      // #1111 — the page hit GET /api/v1/profile/site every 2 s for as long
      // as the Planning tab was open. Edits made here arrive as a 'site'
      // command from Dart, so the poll only covers another client's edits.
      final page = File('assets/stellarium/index.html').readAsStringSync();
      expect(page, contains('setInterval(loadSite, 30000)'));
      expect(page, isNot(contains('setInterval(loadSite, 2000)')));
      // The push path the comment relies on must still exist on both ends.
      expect(page, contains("c.type === 'site'"));
      final dart = File('lib/widgets/sky_atlas/stellarium_view.dart')
          .readAsStringSync();
      expect(dart, contains("'type': 'site'"));
    });
  });

  group('planetarium page framing target', () {
    test('a goto without a name clears the framing name; the search bar sends one', () {
      // Night of 2026-10-02: select the Moon, type "NGC 7000" (a catalog hit →
      // nameless goto), Create Run → a run called "Moon" at NGC 7000. The name
      // must move with the position on the page, and the Dart search path
      // must send it.
      final page = File('assets/stellarium/index.html').readAsStringSync();
      expect(page, contains("frameTargetName = name || '';"));
      expect(page, isNot(contains('if (name) frameTargetName = name;')));
      expect(page, contains('pointRaDec(c.ra, c.dec, c.name);'));
      final dart = File('lib/widgets/sky_atlas/stellarium_view.dart')
          .readAsStringSync();
      expect(dart, contains('planetariumSearchCommand('));
    });
  });

  group('planetarium page touchpad pinch (Linux runner contract)', () {
    test('the page defines the three pinch hooks the runner calls by name', () {
      // #1200/#1275 — linux/runner/planetarium_overlay.cc swallows
      // GDK_TOUCHPAD_PINCH and drives the sky's field of view through
      // pinchBegin/pinchUpdate/pinchEnd on the page. run_js fails silently,
      // so a rename on either side would leave touchpad pinch doing nothing
      // on Linux. No harness runs index.html; this string guard keeps the two
      // sides in step.
      final page = File('assets/stellarium/index.html').readAsStringSync();
      final runner = File('linux/runner/planetarium_overlay.cc')
          .readAsStringSync();
      for (final fn in ['pinchBegin', 'pinchUpdate', 'pinchEnd']) {
        expect(page, contains('function $fn('), reason: '$fn on the page');
        expect(runner, contains('"$fn('), reason: '$fn called by the runner');
      }
      // The scale must be formatted locale-independently (a decimal comma
      // would reach JS as two arguments).
      expect(runner, contains('g_ascii_dtostr('));
      expect(runner, isNot(contains('pinchUpdate(%')));
    });

    test('the runner keeps focus on FlView and takes the Wayland branch', () {
      // #1200 — no C++ harness exists; pin the two by-hand-verified fixes so a
      // revert is caught: the webview and its event box must stay
      // non-focusable (a click on the sky stole keyboard focus from the
      // Planning search field), and gdk_window_ensure_native must be skipped
      // on a Wayland display (it made a parentless, never-mapped toplevel).
      final runner = File('linux/runner/planetarium_overlay.cc')
          .readAsStringSync();
      expect(
        'gtk_widget_set_can_focus'.allMatches(runner).length,
        greaterThanOrEqualTo(2),
        reason: 'webview and event box non-focusable',
      );
      // No gdk_window_ensure_native() call may come back: on Wayland it made
      // a parentless toplevel (#1200), and X11 is refused (#1201). Match the
      // call form so a reworded comment can't mask a real call.
      expect(runner, isNot(contains('gdk_window_ensure_native(window')));
      expect(runner, isNot(matches(RegExp(r'^\s*(if\s*\(.*)?gdk_window_ensure_native\(', multiLine: true))));
      expect(runner, isNot(contains('GDK_IS_WAYLAND_DISPLAY(')));
    });
  });

  group('Linux runner renderer-probe reasons (docs contract)', () {
    test('RUNNING.md quotes every fallback reason the runner can print', () {
      // #1275 — docs/RUNNING.md lists the reasons word for word so users can
      // report them; keep the two in step.
      final runner = File('linux/runner/planetarium_overlay.cc')
          .readAsStringSync();
      // The doc wraps long lines, so compare with whitespace collapsed.
      final doc = File('../../docs/RUNNING.md')
          .readAsStringSync()
          .replaceAll(RegExp(r'\s+'), ' ');
      const reasons = [
        'no DRM render node is present',
        "a previous run's WebKit process crashed in the DMABUF renderer",
        'no DRM render node can back a GBM buffer',
        'libgbm.so.1 is not loadable',
        'the GBM probe did not complete',
      ];
      for (final r in reasons) {
        expect(runner, contains('"$r"'), reason: '$r in the runner');
        expect(doc, contains('`$r`'), reason: '$r in RUNNING.md');
      }
      expect(runner, contains('setting WEBKIT_DISABLE_DMABUF_RENDERER=1'));
      expect(doc, contains('setting WEBKIT_DISABLE_DMABUF_RENDERER=1'));
      // NVIDIA keeps DMABUF and disables the driver's explicit sync instead.
      expect(runner, contains('g_setenv("__NV_DISABLE_EXPLICIT_SYNC", "1", TRUE);'));
      // The runner splits the literal across lines; the doc quotes it whole.
      expect(runner, contains('setting __NV_DISABLE_EXPLICIT_SYNC=1 (DMABUF renderer kept, "'));
      expect(runner, contains('"compositing forced)"'));
      expect(doc, contains('setting __NV_DISABLE_EXPLICIT_SYNC=1 (DMABUF renderer kept, compositing forced)'));
      // The crash backstop: signal hooked, marker path documented.
      expect(runner, contains('"web-process-terminated"'));
      // Clean teardown: SIGTERM routes through GApplication and the overlay
      // destroys the webview so WebKitWebProcess isn't orphaned.
      final app = File('linux/runner/my_application.cc').readAsStringSync();
      expect(app, contains('g_unix_signal_add(SIGTERM, on_terminate_signal'));
      expect(app, contains('planetarium_overlay_shutdown();'));
      // The toplevel is app-paintable so GTK skips its per-frame software
      // background fill (37 % of the UI thread at 4K on NVIDIA Wayland).
      expect(app, contains('gtk_widget_set_app_paintable(GTK_WIDGET(window), TRUE);'));
      // And FlView's background is fully transparent so the embedder skips
      // its own per-frame software paint (50 % of the UI thread at 4K).
      expect(app, contains('gdk_rgba_parse(&background_color, "#00000000");'));
      expect(runner, contains('void planetarium_overlay_shutdown() {'));
      // A normal window close destroys the event box before shutdown runs;
      // the destroy handler clears the pointers so shutdown can't touch
      // freed memory.
      expect(runner, contains('G_CALLBACK(webview_widget_destroyed_cb)'));
      expect(runner, contains('"webkit-no-dmabuf"'));
      expect(doc, contains('webkit-no-dmabuf'));
    });
  });

  group('planetarium page idle render throttle', () {
    test('wraps requestAnimationFrame before the engine starts', () {
      // #1275 — the vendored engine renders unconditionally at display rate;
      // the page throttles it when idle. The wrapper must precede
      // StelWebEngine({ so the engine's loop goes through it.
      final page = File('assets/stellarium/index.html').readAsStringSync();
      final wrapper = page.indexOf('var araIdleThrottle = (function () {');
      final engine = page.indexOf('StelWebEngine({');
      expect(wrapper, greaterThan(-1));
      expect(engine, greaterThan(wrapper));
      expect(page, contains('window.requestAnimationFrame = function (cb) {'));
      expect(page, contains('window.cancelAnimationFrame = function (id) {'));
      // The runner-driven pinch path has no DOM event, so it wakes explicitly.
      expect(page, contains('araIdleThrottle.wake();'));
    });

    test('large views cap the backing resolution and interactive rate', () {
      // #1275 — 4K on the shm renderer: ~100 MB of copies per frame. The
      // caps must be defined before the throttle (which uses them) and the
      // throttle before the engine starts.
      final page = File('assets/stellarium/index.html').readAsStringSync();
      final caps = page.indexOf('var araViewCaps = (function () {');
      final throttle = page.indexOf('var araIdleThrottle = (function () {');
      expect(caps, greaterThan(-1));
      expect(throttle, greaterThan(caps));
      expect(page, contains("Object.defineProperty(window, 'devicePixelRatio'"));
      expect(page, contains('if (!araViewCaps.isLarge()) return nativeRaf(cb);'));
      // Off by default; only the Linux runner turns it on, where a CPU copy
      // per frame remains (shm renderer, or NVIDIA's GTK3-composited DMABUF).
      expect(page, contains('var enabled = false;'));
      // The real ratio must be read live, never frozen at load (review on
      // #1275: a 1x -> 2x monitor move kept the old backing resolution).
      expect(page, contains("Object.getOwnPropertyDescriptor(proto, 'devicePixelRatio')"));
      expect(page, contains('return realDprGetter.call(window) || 1;'));
      expect(page, isNot(contains('var realDpr = window.devicePixelRatio || 1;')));
      final runner = File('linux/runner/planetarium_overlay.cc')
          .readAsStringSync();
      expect(runner, contains('"araViewCaps.enable()"'));
      expect(runner, contains('g_heavy_compositing'));
    });

    test('view-motion detection uses a threshold, not exact equality', () {
      // VM run on #1275: with an object centred, follow-mode drift of ~1e-6
      // rad per frame kept the view "changing" and the throttle never
      // engaged (37 commits/s idle). Motion must be thresholded.
      final page = File('assets/stellarium/index.html').readAsStringSync();
      final body = page.substring(page.indexOf('var araIdleThrottle'));
      expect(body, contains('var MOVE_RAD = 1e-3;'));
      expect(body, contains('Math.abs(v[1] - lastView[1]) > MOVE_RAD'));
      expect(body, isNot(contains('function viewSig()')));
    });

    test('idle callers are queued and flushed together, never one slot', () {
      // Review on #1275: a single throttled slot let the second rAF loop (the
      // page's FOV guard) bounce the engine back to display rate half the
      // time. Pin the queue + single-timer shape.
      final page = File('assets/stellarium/index.html').readAsStringSync();
      final body = page.substring(page.indexOf('var araIdleThrottle'));
      expect(body, contains('queue.push({ id: id, cb: cb });'));
      expect(body, contains('if (!timer) timer = setTimeout(flush, 1000 / IDLE_FPS);'));
      expect(body, isNot(contains('one throttled slot')));
    });
  });

  group('planetarium page scope box', () {
    test('draws the daemon\'s latest solve as a second box and clears on request', () {
      // The by-hand rotation readout pushes {type:'scopeBox', ra, dec, paDeg, fov…}
      // per solve; the page draws it beside the planned framing box.
      final page = File('assets/stellarium/index.html').readAsStringSync();
      expect(page, contains("c.type === 'scopeBox'"));
      expect(page, contains('function scopeBoxData()'));
      expect(page, contains('function updateScopeBox()'));
      expect(page, contains("scopeBox = c.clear ? null :"));
      final dart = File('lib/widgets/sky_atlas/stellarium_view.dart').readAsStringSync();
      expect(dart, contains('scopeBoxCommandFor('));
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
    test('the Frame panel probes the cache and status, and keeps its message', () {
      // No harness runs index.html; this string guard keeps a future edit
      // from silently dropping the two-step probe or the hint. Line endings
      // are normalised: a Windows checkout has CRLF and the multi-line guard
      // below is written with LF.
      final page = File('assets/stellarium/index.html')
          .readAsStringSync()
          .replaceAll('\r\n', '\n');
      expect(page, contains("fetch('./dss/properties', { method: 'HEAD'"));
      expect(page, contains("fetch('./dss/status'"));
      expect(page, contains("Some sky photos for this area aren't cached yet"));
      expect(page, contains('if (frameOn) probeDssPhotos();'));
      // No hint (and no request) for a layer the user turned off — checked
      // inside probe(), so the 4 s re-probe cannot bypass it.
      expect(page, contains("function probe() {\n"
          "    // Photos the user switched off are not \"missing\": say nothing for them,\n"
          "    // and send no request. Inside probe() so the delayed re-probe obeys too.\n"
          "    if (stel && !dispState('dss')) { el.hidden = true; return; }\n"
          "    fetch('./dss/properties'"));
    });
  });
}
