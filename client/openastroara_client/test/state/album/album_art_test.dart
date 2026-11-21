import 'package:flutter/foundation.dart';
import 'package:flutter/services.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/state/album/album_art.dart';

/// Sky Album slice 7 (#1594, docs/specs/sky-album-v1.md "Art and data"): the
/// bundled manifest, its Art and the credits on the About page.
void main() {
  TestWidgetsFlutterBinding.ensureInitialized();

  test('a manifest row reads its art, credit line, distance and trivia; no art is the Placeholder', () {
    final rows = parseAlbumManifest('''[
      {"id": "M31", "art": "m031.webp", "credit": "ESO", "licence": "CC BY 4.0", "source": "https://x",
       "distance": "2.5 million ly", "trivia": "Close."},
      {"id": "M40", "art": null, "credit": null, "licence": null, "distance": "510 ly", "trivia": "A double."},
      {"art": "orphan.webp"}
    ]''');
    expect(rows.keys, ['M31', 'M40'], reason: 'a row without an id is skipped');
    expect(rows['M31']!.asset, 'assets/album/art/m031.webp');
    expect(rows['M31']!.creditLine, 'ESO · CC BY 4.0');
    expect(rows['M40']!.asset, isNull);
    expect(rows['M40']!.creditLine, isNull);
    expect(rows['M40']!.trivia, 'A double.');
    expect(albumArtId('M 31'), 'M31');
  });

  test('each row carries its own credit and licence, so a photographer\'s picture sits beside ESO\'s (#1629)', () {
    final rows = parseAlbumManifest('''[
      {"id": "M31", "art": "m031.webp", "credit": "ESO", "licence": "CC BY 4.0", "source": "https://x"},
      {"id": "RCW38", "art": "rcw038.webp", "credit": "Jane Example (own capture)", "licence": "CC BY 4.0",
       "source": "https://example.invalid/rcw38"},
      {"id": "RCW53", "art": null}
    ]''');
    final row = rows[albumArtId('RCW 38')]!;
    expect((row.asset, row.creditLine), ('assets/album/art/rcw038.webp', 'Jane Example (own capture) · CC BY 4.0'));
    expect((row.licenceUrl, row.source), ('https://creativecommons.org/licenses/by/4.0/', 'https://example.invalid/rcw38'));
    expect(rows['M31']!.creditLine, 'ESO · CC BY 4.0');
    expect(rows['RCW53']!.creditLine, isNull, reason: 'no Art: the Placeholder, no credit line');
  });

  test('a row\'s Portuguese distance and trivia are used in Portuguese, English otherwise', () {
    final rows = parseAlbumManifest('''[
      {"id": "M31", "art": "m031.webp", "credit": "ESO", "licence": "CC BY 4.0", "source": "https://x",
       "distance": "2.5 million ly", "trivia": "Close.", "pt": {"distance": "2,5 milhões de anos-luz", "trivia": "Perto."}},
      {"id": "M40", "art": null, "distance": "510 ly", "trivia": "A double."}
    ]''');
    expect(rows['M31']!.distanceIn('pt'), '2,5 milhões de anos-luz');
    expect(rows['M31']!.triviaIn('pt'), 'Perto.');
    expect(rows['M31']!.triviaIn('en'), 'Close.');
    expect(rows['M40']!.triviaIn('pt'), 'A double.', reason: 'no Portuguese: the English line');
  });

  test('CC BY and CC0 Art links its licence deed; public domain needs none', () {
    String? url(String? l) => AlbumArt(asset: 'a', credit: 'c', licence: l).licenceUrl;
    expect(url('CC BY 4.0'), 'https://creativecommons.org/licenses/by/4.0/');
    expect(url('CC BY 2.5'), 'https://creativecommons.org/licenses/by/2.5/');
    // AstroBin's "Attribution" licence names no version: link the CC BY licence family, not a guessed version.
    expect(url('CC BY'), 'https://creativecommons.org/licenses/by/');
    expect(url('CC0'), 'https://creativecommons.org/publicdomain/zero/1.0/');
    expect(url('Public domain'), isNull);
    expect(url(null), isNull);
  });

  test('the bundled manifest covers both Books and every Art file it names is bundled', () async {
    final rows = parseAlbumManifest(await rootBundle.loadString(albumManifestAsset));
    expect(rows.keys, [for (var n = 1; n <= 110; n++) 'M$n', for (var n = 1; n <= 109; n++) 'C$n']);
    var total = 0;
    for (final MapEntry(:key, :value) in rows.entries) {
      if (value.asset case final asset?) {
        final bytes = (await rootBundle.load(asset)).lengthInBytes;
        total += bytes;
        // About 31 KB a card; a dense star field (M7, M11) stays near 60 KB even at low quality.
        expect(bytes, lessThan(64000), reason: key);
        expect(value.creditLine, isNotNull, reason: '$key: Art needs its credit');
      }
    }
    expect(total, lessThan(8000000), reason: 'about 7 MB for the set');
    for (final MapEntry(:key, :value) in rows.entries) {
      expect(value.pt?.trivia, isNotNull, reason: '$key: Portuguese trivia');
      expect(value.pt?.distance, isNotNull, reason: '$key: Portuguese distance');
    }
  });

  test('every Art credit is in the licence registry', () async {
    registerAlbumArtLicenses();
    final rows = parseAlbumManifest(await rootBundle.loadString(albumManifestAsset));
    final listed = [
      await for (final e in LicenseRegistry.licenses)
        if (e.packages.contains('Sky Album art')) e.paragraphs.map((p) => p.text).join('\n'),
    ];
    for (final MapEntry(:key, :value) in rows.entries) {
      if (value.creditLine case final credit?) {
        // CC BY asks for the licence link and a note that the Art was changed.
        expect(
            listed.any((t) =>
                t.contains('$key: $credit') &&
                t.contains(value.source!) &&
                t.contains(value.licenceUrl ?? '') &&
                t.contains('Cropped and resized')),
            isTrue,
            reason: key);
      }
    }
  });
}
