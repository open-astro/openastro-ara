import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/models/server.dart';
import 'package:openastroara/services/profile_api.dart';
import 'package:openastroara/state/profile_management_state.dart';
import 'package:openastroara/state/settings/site_settings_state.dart';

class _FakeApi extends ProfileApi {
  _FakeApi() : super(const AraServer(hostname: 'test', port: 1));
  @override
  Future<SiteSettings> getSiteSettings() async =>
      const SiteSettings(latitudeDeg: 12.5989, longitudeDeg: -75.8408);
}

void main() {
  group('SiteSettingsNotifier', () {
    late ProviderContainer container;
    setUp(() => container = ProviderContainer());
    tearDown(() => container.dispose());

    test('self-hydrates from the daemon when a server is present', () async {
      final c = ProviderContainer(overrides: [
        profileApiProvider.overrideWithValue(_FakeApi()),
      ]);
      addTearDown(c.dispose);
      // Build the provider (schedules the hydration microtask), then let it
      // land.
      c.read(siteSettingsProvider);
      await pumpEventQueue();
      final s = c.read(siteSettingsProvider);
      expect(s.latitudeDeg, 12.5989);
      expect(s.longitudeDeg, -75.8408);
      expect(s.siteName, 'Backyard'); // default for the unmapped fields
    });

    test('defaults match playbook §37.12', () {
      final s = container.read(siteSettingsProvider);
      expect(s.siteName, 'Backyard');
      expect(s.latitudeDeg, 0);
      expect(s.longitudeDeg, 0);
      expect(s.elevationM, 0);
      expect(s.timeZone, 'UTC');
      expect(s.useCustomHorizon, isFalse);
      expect(s.defaultHorizonAltitudeDeg, 20);
      expect(s.bortleClass, 6);
      expect(s.typicalSeeingArcsec, 2.5);
      expect(s.twilightDefinition, TwilightDefinition.astronomical);
    });

    test('setLatitudeDeg clamps to [-90, 90]', () {
      final n = container.read(siteSettingsProvider.notifier);
      n.setLatitudeDeg(-91);
      n.setLatitudeDeg(91);
      expect(container.read(siteSettingsProvider).latitudeDeg, 0);
      n.setLatitudeDeg(45.5);
      expect(container.read(siteSettingsProvider).latitudeDeg, 45.5);
      n.setLatitudeDeg(-89.999);
      expect(container.read(siteSettingsProvider).latitudeDeg, -89.999);
    });

    test('setLongitudeDeg clamps to [-180, 180]', () {
      final n = container.read(siteSettingsProvider.notifier);
      n.setLongitudeDeg(-181);
      n.setLongitudeDeg(181);
      expect(container.read(siteSettingsProvider).longitudeDeg, 0);
      n.setLongitudeDeg(-118.2437);
      expect(container.read(siteSettingsProvider).longitudeDeg, -118.2437);
    });

    test('setElevationM clamps to [-500, 9000]', () {
      final n = container.read(siteSettingsProvider.notifier);
      n.setElevationM(-1000);
      n.setElevationM(10000);
      expect(container.read(siteSettingsProvider).elevationM, 0);
      n.setElevationM(2400);
      expect(container.read(siteSettingsProvider).elevationM, 2400);
    });

    test('setBortleClass clamps to [1, 9]', () {
      final n = container.read(siteSettingsProvider.notifier);
      n.setBortleClass(0);
      n.setBortleClass(10);
      expect(container.read(siteSettingsProvider).bortleClass, 6);
      n.setBortleClass(2);
      expect(container.read(siteSettingsProvider).bortleClass, 2);
    });

    test('setTypicalSeeingArcsec rejects non-positive + extreme', () {
      final n = container.read(siteSettingsProvider.notifier);
      n.setTypicalSeeingArcsec(0);
      n.setTypicalSeeingArcsec(-1);
      n.setTypicalSeeingArcsec(30);
      expect(container.read(siteSettingsProvider).typicalSeeingArcsec, 2.5);
      n.setTypicalSeeingArcsec(1.8);
      expect(container.read(siteSettingsProvider).typicalSeeingArcsec, 1.8);
    });

    test('setSiteName + setTimeZone trim + reject empty or whitespace-only',
        () {
      final n = container.read(siteSettingsProvider.notifier);
      n.setSiteName('');
      n.setSiteName('   ');
      n.setTimeZone('');
      n.setTimeZone('\t');
      expect(container.read(siteSettingsProvider).siteName, 'Backyard');
      expect(container.read(siteSettingsProvider).timeZone, 'UTC');
      n.setSiteName('  Observatory  ');
      n.setTimeZone('  America/Los_Angeles  ');
      expect(container.read(siteSettingsProvider).siteName, 'Observatory');
      expect(container.read(siteSettingsProvider).timeZone,
          'America/Los_Angeles');
    });

    test('setDefaultHorizonAltitudeDeg clamps to [0, 90]', () {
      final n = container.read(siteSettingsProvider.notifier);
      n.setDefaultHorizonAltitudeDeg(-1);
      n.setDefaultHorizonAltitudeDeg(91);
      expect(container.read(siteSettingsProvider).defaultHorizonAltitudeDeg,
          20);
      n.setDefaultHorizonAltitudeDeg(30);
      expect(container.read(siteSettingsProvider).defaultHorizonAltitudeDeg,
          30);
    });

    test('twilight + custom horizon toggles update directly', () {
      final n = container.read(siteSettingsProvider.notifier);
      n.setTwilightDefinition(TwilightDefinition.nautical);
      n.setUseCustomHorizon(true);
      expect(container.read(siteSettingsProvider).twilightDefinition,
          TwilightDefinition.nautical);
      expect(container.read(siteSettingsProvider).useCustomHorizon, isTrue);
    });
  });
}
