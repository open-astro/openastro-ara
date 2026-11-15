import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/services/profile_api.dart';
import 'package:openastroara/state/settings/unguided_settings_state.dart';

/// #1264 Imaging → Unguided: the model's guards and the wire codec.
void main() {
  test('defaults are the shipped behaviour', () {
    const u = UnguidedSettings();
    expect(u.guardEnabled, isTrue);
    expect(u.eccentricityTolerance, 0.5);
    expect(u.goodEccentricity, 0.3);
    expect(u.stepDownAfter, 2);
    expect(u.stepUpAfter, 10);
    expect(u.probeRungsSeconds, [10, 30, 60, 120]);
    expect(u.recentreDriftArcmin, 10);
    expect(u.recentreSolveEveryN, 1);
    expect(u.priorToleranceBasis, 'max');
    expect(u.probeRungsText, '10, 30, 60, 120');
  });

  test('codec round-trips and tolerates a sparse body', () {
    const u = UnguidedSettings(
      guardEnabled: false,
      eccentricityTolerance: 0.6,
      goodEccentricity: 0.35,
      stepDownAfter: 3,
      stepUpAfter: 8,
      probeRungsSeconds: [5, 20, 80],
      recentreDriftArcmin: 4.5,
      recentreSolveEveryN: 2,
      priorToleranceBasis: 'seeing',
    );
    final j = ProfileApi.unguidedSettingsToJson(u);
    expect(j['probe_rungs_seconds'], [5, 20, 80]);
    expect(j['prior_tolerance_basis'], 'seeing');
    final back = ProfileApi.unguidedSettingsFromJson(j);
    expect(back.guardEnabled, isFalse);
    expect(back.eccentricityTolerance, 0.6);
    expect(back.probeRungsSeconds, [5, 20, 80]);
    expect(back.recentreDriftArcmin, 4.5);
    expect(back.priorToleranceBasis, 'seeing');
    final sparse = ProfileApi.unguidedSettingsFromJson(const {
      'probe_rungs_seconds': [],
    });
    expect(
      sparse.probeRungsSeconds,
      UnguidedSettings.defaultProbeRungsSeconds,
      reason: 'an empty ladder is the default ladder',
    );
    expect(sparse.guardEnabled, isTrue);
  });

  test('parseRungs accepts an ascending positive list and nothing else', () {
    expect(UnguidedSettings.parseRungs('10, 30, 60'), [10, 30, 60]);
    expect(UnguidedSettings.parseRungs('5 15 45.5'), [5, 15, 45.5]);
    expect(
      UnguidedSettings.parseRungs('30, 10'),
      isNull,
      reason: 'not ascending',
    );
    expect(UnguidedSettings.parseRungs('10, 10'), isNull, reason: 'duplicate');
    expect(UnguidedSettings.parseRungs('0, 10'), isNull);
    expect(UnguidedSettings.parseRungs('ten'), isNull);
    expect(UnguidedSettings.parseRungs(''), isNull);
  });

  test(
    'the notifier keeps the two eccentricity bars ordered and rejects junk',
    () {
      final c = ProviderContainer();
      addTearDown(c.dispose);
      final n = c.read(unguidedSettingsProvider.notifier);
      n.setGoodEccentricity(0.6);
      expect(
        c.read(unguidedSettingsProvider).goodEccentricity,
        0.3,
        reason: 'must stay below the trailed bar',
      );
      n.setEccentricityTolerance(0.25);
      expect(c.read(unguidedSettingsProvider).eccentricityTolerance, 0.25);
      expect(
        c.read(unguidedSettingsProvider).goodEccentricity,
        0.125,
        reason: 'the round bar follows down',
      );
      n.setEccentricityTolerance(1.5);
      expect(
        c.read(unguidedSettingsProvider).eccentricityTolerance,
        0.25,
        reason: '> 1 ignored',
      );
      n.setStepDownAfter(0);
      expect(c.read(unguidedSettingsProvider).stepDownAfter, 2);
      n.setPriorToleranceBasis('whatever');
      expect(c.read(unguidedSettingsProvider).priorToleranceBasis, 'max');
      n.setPriorToleranceBasis('pixel');
      expect(c.read(unguidedSettingsProvider).priorToleranceBasis, 'pixel');
      n.setRecentreDriftArcmin(-1);
      expect(c.read(unguidedSettingsProvider).recentreDriftArcmin, 10);
    },
  );
}
