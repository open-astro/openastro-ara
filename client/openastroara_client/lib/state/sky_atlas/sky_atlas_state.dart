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
  // WARNING: because this always notifies, do NOT `ref.watch` this provider for
  // a rebuild — every search submission (incl. duplicates) would rebuild the
  // watcher. Consume it with `ref.listen` (a side-effect) instead.
  @override
  bool updateShouldNotify(String previous, String next) => true;
}

final skyAtlasSearchProvider =
    NotifierProvider<SkyAtlasSearchNotifier, String>(
        SkyAtlasSearchNotifier.new);

/// §36 — a chosen sky target (equatorial J2000 coordinates + a display name).
/// The planetarium (`StellariumView`) flies to it; a connected mount can GoTo it.
class SkyTarget {
  final double raDeg;
  final double decDeg;
  final String name;
  const SkyTarget({required this.raDeg, required this.decDeg, required this.name});
}

/// The currently-selected planetarium target, or null when nothing is selected.
/// Tonight's Sky (and, later, the search bar) set this; `StellariumView` listens
/// and centres the view. Like the search notifier it always notifies, so
/// re-selecting the same object re-centres — consume it with `ref.listen`, not
/// `ref.watch`.
class SkyTargetNotifier extends Notifier<SkyTarget?> {
  @override
  SkyTarget? build() => null;
  void set(SkyTarget target) => state = target;
  void clear() => state = null;

  @override
  bool updateShouldNotify(SkyTarget? previous, SkyTarget? next) => true;
}

final skyTargetProvider =
    NotifierProvider<SkyTargetNotifier, SkyTarget?>(SkyTargetNotifier.new);

/// Whether the active profile has at least one HiPS sky-imagery survey
/// downloaded. Drives the §36.13 sky-data-missing banner. Phase 12e.1 stubs
/// this to false (no downloads yet); 12e.2 reads it from the Data Manager
/// state once download tracking lands.
final skyImageryAvailableProvider = Provider<bool>((ref) => false);

/// A selectable Aladin base imagery survey (§36 "best-looking sky" picker). The
/// [id] is the CDS HiPS identifier handed to Aladin's `setBaseImageLayer`; the
/// [coverage] note tells the user a partial survey leaves the rest of the sky
/// blank (not a bug).
class SkySurvey {
  final String id;
  final String label;
  final String coverage;
  const SkySurvey(this.id, this.label, this.coverage);
}

/// The atlas's default base survey — full-sky colour photographs (DSS2). The
/// Aladin bootstrap also hard-codes this as its initial `survey`, so the two
/// must stay in sync (the view only pushes a survey change when it differs).
const String kDefaultSkySurveyId = 'P/DSS2/color';

/// The curated survey choices, verified against the CDS HiPS list. DSS2 colour
/// is first (the full-sky default); the partial-coverage deep surveys follow,
/// each flagged so the user understands the gaps.
const List<SkySurvey> kSkySurveys = [
  SkySurvey(kDefaultSkySurveyId, 'DSS2 colour', 'Full sky'),
  SkySurvey('CDS/P/DESI-Legacy-Surveys/DR10/color', 'DESI Legacy DR10',
      'Partial — deepest colour'),
  SkySurvey('CDS/P/DECaPS/DR2/color', 'DECaPS DR2',
      'Partial — southern Milky Way'),
  SkySurvey('CDS/P/2MASS/color', '2MASS (infrared)', 'Full sky'),
];

/// The currently-selected base survey id. The Planning header's picker sets it;
/// [AladinView] listens and calls `setBaseImageLayer`. Defaults to DSS2 colour.
class SkyAtlasSurveyNotifier extends Notifier<String> {
  @override
  String build() => kDefaultSkySurveyId;
  void set(String id) => state = id;
}

final skyAtlasSurveyProvider =
    NotifierProvider<SkyAtlasSurveyNotifier, String>(
        SkyAtlasSurveyNotifier.new);
