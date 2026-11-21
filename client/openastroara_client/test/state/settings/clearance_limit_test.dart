import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/services/profile_api.dart';
import 'package:openastroara/state/settings/safety_policies_state.dart';

import '../../support/fake_section_rig.dart';
import '../../support/section_load.dart';

/// #1589 — the clearance limit rides the rig's safety section: read as the rig
/// has it, saved whole with If-Match, and never made up by the client.
void main() {
  Map<String, Object?> rigSafety([Map<String, Object?> change = const {}]) => {
    ...rigDefaults['safety']!,
    ...change,
  };

  test(
    'the clearance limit reads as the rig has it and a Save sends it back',
    () async {
      final rig = FakeSectionRig({
        'safety': rigSafety({
          'clearance_limit_enabled': true,
          'clearance_max_altitude_deg': 75.5,
        }),
      });
      final c = ProviderContainer();
      addTearDown(c.dispose);
      expect(
        await c.read(safetyPoliciesProvider.notifier).load(rig.api()),
        isTrue,
      );
      final shown = c.read(safetyPoliciesProvider).value!;
      expect(shown.clearanceLimitEnabled, isTrue);
      expect(shown.clearanceMaxAltitudeDeg, 75.5);

      c.read(safetyPoliciesProvider.notifier).setClearanceMaxAltitudeDeg(70);
      await c.read(safetyPoliciesProvider.notifier).save(rig.api());

      final body = rig.puts.single.body as Map;
      expect(body['clearance_limit_enabled'], isTrue);
      expect(body['clearance_max_altitude_deg'], 70.0);
      expect(
        rig.puts.single.ifMatch,
        isNotNull,
        reason: 'saved over the rig copy',
      );
    },
  );

  test('a rig reply without the clearance limit fails the load and Save sends no PUT', () async {
    final rig = FakeSectionRig({
      'safety': rigSafety()..remove('clearance_limit_enabled'),
    });
    final c = ProviderContainer();
    addTearDown(c.dispose);

    expect(
      await c.read(safetyPoliciesProvider.notifier).load(rig.api()),
      isFalse,
    );
    expect(c.read(safetyPoliciesProvider).value, isNull);
    await expectLater(
      c.read(safetyPoliciesProvider.notifier).save(rig.api()),
      throwsA(anything),
    );
    expect(rig.puts, isEmpty);
  });

  test(
    'the highest safe altitude keeps only values above 0 and under 90 degrees',
    () async {
      final c = ProviderContainer();
      addTearDown(c.dispose);
      await loadSection(c, safetyPoliciesProvider, 'safety');
      final n = c.read(safetyPoliciesProvider.notifier);

      n.setClearanceMaxAltitudeDeg(78);
      for (final bad in [0.0, -3.0, 90.0, 95.0, double.nan]) {
        n.setClearanceMaxAltitudeDeg(bad);
      }

      expect(c.read(safetyPoliciesProvider).value!.clearanceMaxAltitudeDeg, 78);
    },
  );

  test('the clearance limit keys are modeled, not passed through', () {
    final json = ProfileApi.safetyPoliciesToJson(
      const SafetyPolicies(clearanceLimitEnabled: true),
    );
    expect(json['clearance_limit_enabled'], isTrue);
    expect(json.containsKey('clearance_max_altitude_deg'), isTrue);
  });
}
