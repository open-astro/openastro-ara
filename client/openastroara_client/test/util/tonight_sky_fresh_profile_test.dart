import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/state/settings/optics_settings_state.dart';
import 'package:openastroara/state/settings/site_settings_state.dart';
import 'package:openastroara/util/tonight_sky_local.dart';

/// Tonight's Sky on a *fresh* profile: a site has been entered but nothing
/// else — default optics, no filter set, no catalog downloaded yet (the
/// starter catalog stands in). This is the state of every first-run install
/// and of a test VM straight after `vm.sh deploy`, and the panel's empty
/// state ("Nothing well-placed tonight. Set your site location…") is
/// indistinguishable from a ranking bug, so the ranking itself must never
/// come back empty for a real site on any night of the year.
///
/// Extend this file as client platforms come online (see the ara-sbc-vm and
/// ara-pr-investigation skills): the same inputs must rank the same on
/// Windows, macOS, Linux, Android and iOS — the Isolate path in
/// tonight_sky_state.dart is the only per-platform difference.
void main() {
  const sites = <String, SiteSettings>{
    'Los Angeles (mid-north)': SiteSettings(
      siteName: 'LA',
      latitudeDeg: 34.05,
      longitudeDeg: -118.25,
      timeZone: 'America/Los_Angeles',
    ),
    'Tromsø (arctic, no astro dark in summer)': SiteSettings(
      siteName: 'Tromsø',
      latitudeDeg: 69.65,
      longitudeDeg: 18.96,
      timeZone: 'Europe/Oslo',
    ),
    'Quito (equator)': SiteSettings(
      siteName: 'Quito',
      latitudeDeg: -0.18,
      longitudeDeg: -78.47,
      timeZone: 'America/Guayaquil',
    ),
    'Hobart (deep south)': SiteSettings(
      siteName: 'Hobart',
      latitudeDeg: -42.88,
      longitudeDeg: 147.33,
      timeZone: 'Australia/Hobart',
    ),
  };

  // Every month, at three wall-clock moments (local morning, afternoon,
  // late evening ≈ UTC offsets don't matter: the ranking anchors on the
  // coming night from whatever "now" is).
  final moments = <DateTime>[
    for (var month = 1; month <= 12; month++)
      for (final hour in const [2, 14, 21]) DateTime.utc(2026, month, 15, hour),
  ];

  for (final entry in sites.entries) {
    final arctic = entry.key.startsWith('Tromsø');
    test('fresh profile at ${entry.key} ranks something every month', () {
      for (final at in moments) {
        final list = computeTonightSkyLocal(
          site: entry.value,
          optics: const OpticsSettings(),
          atUtc: at,
          limit: 30,
        );
        // Above the arctic circle astronomical dark is gone from roughly
        // mid-April to late September, so an empty list there is correct, not a
        // bug (the panel's empty state is the right answer for once).
        if (arctic && at.month >= 4 && at.month <= 9) continue;
        expect(list, isNotEmpty, reason: 'empty list at $at for ${entry.key}');
        for (final o in list) {
          expect(o.score, inInclusiveRange(0, 100), reason: '${o.name} at $at');
        }
      }
    });
  }

  test('a (0, 0) site is the "not set" sentinel and is handled upstream', () {
    // tonight_sky_state.dart short-circuits before ranking; the ranking itself
    // still copes if called directly so a stale cache can never crash it.
    final list = computeTonightSkyLocal(
      site: const SiteSettings(),
      optics: const OpticsSettings(),
      atUtc: DateTime.utc(2026, 9, 24, 20),
    );
    expect(list, isA<List<Object?>>());
  });
}
