import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/services/profile_api.dart';
import 'package:openastroara/state/settings/site_settings_state.dart';

import '../../support/section_load.dart';

void main() {
  group('SiteSettingsNotifier', () {
    late ProviderContainer container;
    // ADR 0015: an edit counts only on a copy the rig served (its defaults).
    setUp(() async {
      container = ProviderContainer();
      await loadSection(container, siteSettingsProvider, 'site');
    });
    tearDown(() => container.dispose());

    test('the rig\'s defaults match playbook §37.12', () {
      final s = container.read(siteSettingsProvider).value!;
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
      expect(container.read(siteSettingsProvider).value!.latitudeDeg, 0);
      n.setLatitudeDeg(45.5);
      expect(container.read(siteSettingsProvider).value!.latitudeDeg, 45.5);
      n.setLatitudeDeg(-89.999);
      expect(container.read(siteSettingsProvider).value!.latitudeDeg, -89.999);
    });

    test('setLongitudeDeg clamps to [-180, 180]', () {
      final n = container.read(siteSettingsProvider.notifier);
      n.setLongitudeDeg(-181);
      n.setLongitudeDeg(181);
      expect(container.read(siteSettingsProvider).value!.longitudeDeg, 0);
      n.setLongitudeDeg(-118.2437);
      expect(container.read(siteSettingsProvider).value!.longitudeDeg, -118.2437);
    });

    test('setElevationM clamps to [-500, 9000]', () {
      final n = container.read(siteSettingsProvider.notifier);
      n.setElevationM(-1000);
      n.setElevationM(10000);
      expect(container.read(siteSettingsProvider).value!.elevationM, 0);
      n.setElevationM(2400);
      expect(container.read(siteSettingsProvider).value!.elevationM, 2400);
    });

    test('elevation is whole metres (a GPS fix, a typed decimal, the saved profile)', () {
      final n = container.read(siteSettingsProvider.notifier);
      n.setElevationM(71.12);
      expect(container.read(siteSettingsProvider).value!.elevationM, 71);
      n.setElevationM(-2.5);
      expect(container.read(siteSettingsProvider).value!.elevationM, -3);
      expect(ProfileApi.siteSettingsFromJson({'elevation_m': 71.6}).elevationM, 72);
    });

    test('setSkyBortle rejects out-of-range and clears the SQM', () {
      final n = container.read(siteSettingsProvider.notifier);
      n.setSkySqm(20.3);
      n.setSkyBortle(0);
      n.setSkyBortle(10);
      expect(container.read(siteSettingsProvider).value!.sqmMagPerArcsec2, 20.3);
      n.setSkyBortle(2);
      final s = container.read(siteSettingsProvider).value!;
      expect(s.bortleClass, 2);
      expect(s.sqmMagPerArcsec2, 0, reason: 'no hidden reading after the switch');
    });

    test('setSkySqm stores the reading and the derived class; rejects 0 and out-of-range', () {
      final n = container.read(siteSettingsProvider.notifier);
      n.setSkySqm(20.3);
      var s = container.read(siteSettingsProvider).value!;
      expect(s.sqmMagPerArcsec2, 20.3);
      expect(s.bortleClass, 4);
      n.setSkySqm(0);
      n.setSkySqm(15.9);
      n.setSkySqm(22.6);
      s = container.read(siteSettingsProvider).value!;
      expect(s.sqmMagPerArcsec2, 20.3);
      expect(s.bortleClass, 4);
    });

    test('setTypicalSeeingArcsec rejects non-positive + extreme', () {
      final n = container.read(siteSettingsProvider.notifier);
      n.setTypicalSeeingArcsec(0);
      n.setTypicalSeeingArcsec(-1);
      n.setTypicalSeeingArcsec(30);
      expect(container.read(siteSettingsProvider).value!.typicalSeeingArcsec, 2.5);
      n.setTypicalSeeingArcsec(1.8);
      expect(container.read(siteSettingsProvider).value!.typicalSeeingArcsec, 1.8);
    });

    test('setSiteName + setTimeZone trim + reject empty or whitespace-only',
        () {
      final n = container.read(siteSettingsProvider.notifier);
      n.setSiteName('');
      n.setSiteName('   ');
      n.setTimeZone('');
      n.setTimeZone('\t');
      expect(container.read(siteSettingsProvider).value!.siteName, 'Backyard');
      expect(container.read(siteSettingsProvider).value!.timeZone, 'UTC');
      n.setSiteName('  Observatory  ');
      n.setTimeZone('  America/Los_Angeles  ');
      expect(container.read(siteSettingsProvider).value!.siteName, 'Observatory');
      expect(container.read(siteSettingsProvider).value!.timeZone,
          'America/Los_Angeles');
    });

    test('setDefaultHorizonAltitudeDeg clamps to [0, 90]', () {
      final n = container.read(siteSettingsProvider.notifier);
      n.setDefaultHorizonAltitudeDeg(-1);
      n.setDefaultHorizonAltitudeDeg(91);
      expect(container.read(siteSettingsProvider).value!.defaultHorizonAltitudeDeg,
          20);
      n.setDefaultHorizonAltitudeDeg(30);
      expect(container.read(siteSettingsProvider).value!.defaultHorizonAltitudeDeg,
          30);
    });

    test('twilight + custom horizon toggles update directly', () {
      final n = container.read(siteSettingsProvider.notifier);
      n.setTwilightDefinition(TwilightDefinition.nautical);
      n.setUseCustomHorizon(true);
      expect(container.read(siteSettingsProvider).value!.twilightDefinition,
          TwilightDefinition.nautical);
      expect(container.read(siteSettingsProvider).value!.useCustomHorizon, isTrue);
    });
  });
}
