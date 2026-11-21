import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';

import '../../support/fake_section_rig.dart';
import '../../support/section_load.dart';

import 'package:openastroara/services/profile_api.dart';
import 'package:openastroara/state/sequencer/create_imaging_run.dart'
    show unguidedOptionsOf;
import 'package:openastroara/state/settings/loaded_section.dart';
import 'package:openastroara/state/settings/unguided_settings_state.dart';
import '../../support/settings_values.dart';

/// #1264 Imaging → Unguided: the model's guards and the wire codec.
void main() {
  test('codec round-trips, and a sparse body fails the read', () {
    const u = TestUnguidedSettings(
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
    // ADR 0015 rule 3: a key the body leaves out is missing, not the old
    // client default.
    expect(
      () => ProfileApi.unguidedSettingsFromJson(const {'probe_rungs_seconds': []}),
      throwsA(anything),
    );
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
    () async {
      final c = ProviderContainer();
      addTearDown(c.dispose);
      await loadSection(c, unguidedSettingsProvider, 'unguided');
      final n = c.read(unguidedSettingsProvider.notifier);
      n.setGoodEccentricity(0.6);
      expect(
        c.read(unguidedSettingsProvider).value!.goodEccentricity,
        0.3,
        reason: 'must stay below the trailed bar',
      );
      n.setEccentricityTolerance(0.25);
      expect(
        c.read(unguidedSettingsProvider).value!.eccentricityTolerance,
        0.25,
      );
      expect(
        c.read(unguidedSettingsProvider).value!.goodEccentricity,
        0.125,
        reason: 'the round bar follows down',
      );
      n.setEccentricityTolerance(1.5);
      expect(
        c.read(unguidedSettingsProvider).value!.eccentricityTolerance,
        0.25,
        reason: '> 1 ignored',
      );
      n.setStepDownAfter(0);
      expect(c.read(unguidedSettingsProvider).value!.stepDownAfter, 2);
      n.setPriorToleranceBasis('whatever');
      expect(
        c.read(unguidedSettingsProvider).value!.priorToleranceBasis,
        'max',
      );
      n.setPriorToleranceBasis('pixel');
      expect(
        c.read(unguidedSettingsProvider).value!.priorToleranceBasis,
        'pixel',
      );
      n.setRecentreDriftArcmin(-1);
      expect(c.read(unguidedSettingsProvider).value!.recentreDriftArcmin, 10);
    },
  );

  test("the daemon's own ladder (null) is saved back as null, never as a "
      'fixed list', () async {
    final rig = FakeSectionRig({'unguided': rigDefaults['unguided']});
    final c = ProviderContainer();
    addTearDown(c.dispose);
    final n = c.read(unguidedSettingsProvider.notifier);
    await n.load(rig.api());
    n.setGuardEnabled(false);
    await n.save(rig.api());
    expect((rig.puts.single.body as Map)['probe_rungs_seconds'], isNull);
    expect(
      c.read(unguidedSettingsProvider).value!.probeRungsText,
      '10, 30, 60, 120',
    );
    expect(unguidedOptionsOf(c).probeRungsSeconds, [10, 30, 60, 120]);
  });

  test('not loaded: the run builder refuses, never a client default', () {
    final c = ProviderContainer();
    addTearDown(c.dispose);
    expect(c.read(unguidedSettingsProvider).value, isNull);
    expect(() => unguidedOptionsOf(c), throwsA(isA<SectionNotLoadedException>()));
  });
}
