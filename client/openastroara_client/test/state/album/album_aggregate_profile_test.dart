import 'dart:io';
import 'dart:math' as math;

import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/services/bundled_catalogs.dart';
import 'package:openastroara/state/album/album_frame.dart';
import 'package:openastroara/state/album/album_matching.dart';

/// #1887: the six Books (~14k Cards with NGC, IC and Sharpless) matched against a big
/// collection, timed. The Album runs this on the UI isolate for every Book each time the
/// collection changes, so a scan of every object for every frame froze it. The assertion is a
/// loose ceiling that only catches that kind of regression; the measured number is printed for
/// the issue record. Dart VM JIT here, as in tonight_sky_window_scan_profile_test.dart.
void main() {
  test('matching the six bundled Books against 2,000 position and 200 name-only frames stays under a loose 2 s ceiling', () {
    final bundle = parseBundledCatalogs([
      for (final a in bundledCatalogAssets) File(a).readAsStringSync(),
    ]);
    final books = {for (final b in AlbumBook.values) b: bookObjects(bundle, b)};
    final rnd = math.Random(1887);
    final start = DateTime.utc(2026, 1, 1, 20);
    final frames = <AlbumFrame>[];
    for (var i = 0; i < 2000; i++) {
      // Pointed near a Book object, the way lights are, with the profile rig of the window-scan
      // test: 6248 x 4176 px at 3.1"/px, a 1.8 deg circle.
      final book =
          books[[AlbumBook.ngc, AlbumBook.ic, AlbumBook.sharpless][i % 3]]!;
      final o = book[rnd.nextInt(book.length)];
      frames.add(
        AlbumFrame(
          frameId: 'p$i',
          raDeg: (o.raDeg + rnd.nextDouble() - 0.5) % 360,
          decDeg: (o.decDeg + rnd.nextDouble() - 0.5).clamp(-90.0, 90.0),
          width: 6248,
          height: 4176,
          pixelScaleArcsec: 3.1,
          exposureSeconds: 300,
          capturedUtc: start.add(Duration(minutes: 5 * i)),
        ),
      );
    }
    for (var i = 0; i < 200; i++) {
      // No pointing stored: matched by the target name alone.
      frames.add(
        AlbumFrame(
          frameId: 'n$i',
          targetName: bundle[rnd.nextInt(bundle.length)].id,
          exposureSeconds: 300,
          capturedUtc: start.add(Duration(minutes: 5 * (2000 + i))),
        ),
      );
    }

    // Warm the JIT on a slice of each kind, then time every Book against the whole collection
    // three times.
    aggregateBook([
      ...frames.take(25),
      ...frames.skip(2000).take(25),
    ], books[AlbumBook.messier]!);
    final ms = <int>[];
    var collected = <int>[];
    for (var run = 0; run < 3; run++) {
      final sw = Stopwatch()..start();
      collected = [
        for (final book in books.values) aggregateBook(frames, book).length,
      ];
      sw.stop();
      ms.add(sw.elapsedMilliseconds);
    }
    ms.sort();
    // ignore: avoid_print
    print(
      '[#1887] six Books (${books.values.map((b) => b.length).join(' + ')} Cards) against '
      '${frames.length} frames: min ${ms.first} ms, median ${ms[1]} ms, max ${ms.last} ms '
      '(Dart VM JIT), Cards filled $collected',
    );
    expect(
      collected.reduce((a, b) => a + b),
      greaterThan(2000),
      reason: 'the frames do fill Cards',
    );
    expect(
      ms[1],
      lessThan(2000),
      reason: 'a loose ceiling: catches an algorithmic regression, not jitter',
    );
  });
}
