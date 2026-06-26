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
    test('unknown / binary sky-data blobs fall back to octet-stream', () {
      expect(StellariumServer.contentTypeFor('/skydata/dso/Norder0/Dir0/Npix0.eph').mimeType,
          'application/octet-stream');
    });
  });
}
