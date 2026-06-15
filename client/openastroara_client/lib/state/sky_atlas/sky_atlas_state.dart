import 'package:flutter_riverpod/flutter_riverpod.dart';

/// State for the Sky Atlas tab (§25.5.4 + §36).
///
/// Phase 12e.1 ships:
///   - mode toggle (Catalog View vs Tonight's Sky)
///   - search query string
///   - "any Sky Imagery downloaded?" flag (drives the §36.13
///     sky-data-missing banner)
///
/// 12e.2 wires the real Aladin Lite webview + the universal search backend.

enum SkyAtlasMode { catalogView, tonightsSky }

class SkyAtlasModeNotifier extends Notifier<SkyAtlasMode> {
  @override
  SkyAtlasMode build() => SkyAtlasMode.catalogView;
  void set(SkyAtlasMode m) => state = m;
}

final skyAtlasModeProvider =
    NotifierProvider<SkyAtlasModeNotifier, SkyAtlasMode>(
        SkyAtlasModeNotifier.new);

class SkyAtlasSearchNotifier extends Notifier<String> {
  @override
  String build() => '';
  void set(String q) => state = q;

  // Re-emit even when the submitted target is unchanged, so resubmitting the
  // same name (e.g. after panning away) still recenters the atlas. Only
  // AladinView.listen consumes this — nothing watches it for a rebuild — so
  // always notifying has no rebuild cost.
  @override
  bool updateShouldNotify(String previous, String next) => true;
}

final skyAtlasSearchProvider =
    NotifierProvider<SkyAtlasSearchNotifier, String>(
        SkyAtlasSearchNotifier.new);

/// Whether the active profile has at least one HiPS sky-imagery survey
/// downloaded. Drives the §36.13 sky-data-missing banner. Phase 12e.1 stubs
/// this to false (no downloads yet); 12e.2 reads it from the Data Manager
/// state once download tracking lands.
final skyImageryAvailableProvider = Provider<bool>((ref) => false);
