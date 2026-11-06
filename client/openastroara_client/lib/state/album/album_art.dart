/// Sky Album Art and card data (docs/specs/sky-album-v1.md, "Art and data"):
/// the bundled manifest, one row per Card, keyed by its catalog label
/// (`M31`, `C14`). Written by scripts/album-art.py; a Card with no row, or a
/// row with no art, shows the Placeholder.
library;

import 'dart:convert';

import 'package:flutter/foundation.dart';
import 'package:flutter/services.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

const String albumManifestAsset = 'assets/album/manifest.json';

class AlbumArt {
  /// The bundled WebP's asset path, null when the Card shows the Placeholder.
  final String? asset;
  final String? credit;
  final String? licence;
  final String? source;
  final String? distance;
  final String? trivia;

  const AlbumArt({this.asset, this.credit, this.licence, this.source, this.distance, this.trivia});

  /// The card back's line, `ESO · CC BY 4.0`; null without art.
  String? get creditLine => asset == null || credit == null ? null : [credit!, ?licence].join(' · ');

  /// The licence deed CC BY asks the credit to link; null for public domain.
  String? get licenceUrl => switch (licence) {
        'CC0' || 'CC0 1.0' => 'https://creativecommons.org/publicdomain/zero/1.0/',
        final l? when l.startsWith('CC BY ') => 'https://creativecommons.org/licenses/by/${l.substring(6)}/',
        _ => null,
      };
}

String? _str(dynamic v) => v is String && v.trim().isNotEmpty ? v.trim() : null;

/// The manifest's rows by id. Rows without an id are skipped.
Map<String, AlbumArt> parseAlbumManifest(String text) {
  final decoded = jsonDecode(text);
  if (decoded is! List) return const {};
  return {
    for (final r in decoded.whereType<Map<String, dynamic>>())
      ?_str(r['id']): AlbumArt(
        asset: switch (_str(r['art'])) {
          null => null,
          final file => 'assets/album/art/$file',
        },
        credit: _str(r['credit']),
        licence: _str(r['licence']),
        source: _str(r['source']),
        distance: _str(r['distance']),
        trivia: _str(r['trivia']),
      ),
  };
}

/// The manifest id of a Book's card: `M31`, `C14`.
String albumArtId(String label) => label.replaceAll(' ', '');

/// The bundled manifest; empty (every Card a Placeholder) when the asset is missing.
final albumManifestProvider = FutureProvider<Map<String, AlbumArt>>((ref) async {
  try {
    return parseAlbumManifest(await rootBundle.loadString(albumManifestAsset));
  } catch (_) {
    return const {};
  }
});

/// Lists every Art credit with its source and licence link on the About page's licences.
void registerAlbumArtLicenses() {
  LicenseRegistry.addLicense(() async* {
    final Map<String, AlbumArt> rows;
    try {
      rows = parseAlbumManifest(await rootBundle.loadString(albumManifestAsset));
    } catch (_) {
      return;
    }
    for (final e in rows.entries) {
      final a = e.value;
      if (a.creditLine == null) continue;
      // CC BY 2.0 and later ask that a change to the work is marked.
      yield LicenseEntryWithLineBreaks(['Sky Album art'], [
        '${e.key}: ${a.creditLine}',
        ?a.source,
        ?a.licenceUrl,
        'Cropped and resized for the card.',
      ].join('\n'));
    }
  });
}
