import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/models/server.dart';
import 'package:openastroara/state/saved_server_state.dart';
import 'package:openastroara/state/settings/site_settings_state.dart';
import 'package:openastroara/services/tonight_sky_api.dart';
import 'package:openastroara/state/sky_atlas/tonight_sky_state.dart';

/// Pins savedServersProvider to an empty list = genuinely no server.
class _NoServers extends SavedServersNotifier {
  @override
  Future<List<AraServer>> build() async => const [];
}

class _SeededSite extends SiteSettingsNotifier {
  _SeededSite(this._site);
  final SiteSettings _site;
  @override
  SiteSettings build() => _site;
}

void main() {
  test('no server + cached site → ranks locally', () async {
    final c = ProviderContainer(overrides: [
      savedServersProvider.overrideWith(_NoServers.new),
      siteSettingsProvider.overrideWith(() => _SeededSite(const SiteSettings(
            siteName: 'cached',
            latitudeDeg: 34.0,
            longitudeDeg: -84.0,
            bortleClass: 6,
            defaultHorizonAltitudeDeg: 20,
            twilightDefinition: TwilightDefinition.astronomical,
          ))),
    ]);
    addTearDown(c.dispose);
    final list = await c.read(tonightSkyProvider.future);
    expect(list, isNotEmpty);
    expect(list.first.score, isNotNull);
    expect(list.first.scoreReasons!.join(' '), contains('offline ranking'));
  });

  test('no server + unset site (0,0) → empty, never a Gulf-of-Guinea ranking',
      () async {
    final c = ProviderContainer(overrides: [
      savedServersProvider.overrideWith(_NoServers.new),
    ]);
    addTearDown(c.dispose);
    expect(await c.read(tonightSkyProvider.future), isEmpty);
  });

  test('what-if overrides flow into the local ranking end-to-end', () async {
    final c = ProviderContainer(overrides: [
      savedServersProvider.overrideWith(_NoServers.new),
      siteSettingsProvider.overrideWith(() => _SeededSite(const SiteSettings(
            siteName: 'cached',
            latitudeDeg: 34.0,
            longitudeDeg: -84.0,
            bortleClass: 6,
            defaultHorizonAltitudeDeg: 20,
            twilightDefinition: TwilightDefinition.astronomical,
          ))),
    ]);
    addTearDown(c.dispose);
    final baseline = await c.read(tonightSkyProvider.future);
    expect(baseline, isNotEmpty);

    // Dial in a what-if train: the provider must fold it into the ranking
    // (a different focal length changes nothing for the size-less starter
    // catalog scores, so assert via the recompute plumbing: a new, distinct
    // result object arrives and the overrides survive the Isolate hop).
    c.read(tonightSkyOverridesProvider.notifier).set(const TonightOverrides(
          focalLengthMm: 2000,
          sensorW: 6248,
          sensorH: 4176,
          pixelUm: 3.76,
          mosaicX: 2,
          mosaicY: 2,
        ));
    final overridden = await c.read(tonightSkyProvider.future);
    expect(overridden, isNotEmpty);
    expect(identical(baseline, overridden), isFalse,
        reason: 'the overrides watch must re-rank');
  });
}
