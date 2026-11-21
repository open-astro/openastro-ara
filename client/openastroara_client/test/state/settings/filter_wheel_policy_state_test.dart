import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';

import 'package:openastroara/services/profile_api.dart';
import 'package:openastroara/state/profile_live_sync.dart';
import 'package:openastroara/state/profile_management_state.dart';

import '../../support/fake_section_rig.dart';

import 'package:openastroara/state/settings/filter_wheel_policy_state.dart';
import 'package:openastroara/state/settings/migrated_sections.dart';

void main() {
  group('FilterWheelPolicyNotifier (#1075)', () {
    late ProviderContainer container;
    late FakeSectionRig rig;
    setUp(() async {
      ProfileApi.forgetOwnWrites();
      rig = FakeSectionRig({
        'filter-wheel/policy': rigDefaults['filter-wheel/policy'],
        'filter-wheel/labels': rigDefaults['filter-wheel/labels'],
      });
      container = ProviderContainer(
        overrides: [profileApiProvider.overrideWithValue(rig.api())],
      );
      // Self-loading: wait for the rig's copy.
      container.read(filterWheelPolicyProvider);
      await settle(() => container.read(filterWheelPolicyProvider).canSave);
    });
    tearDown(() => container.dispose());

    test("the rig's copy has home on first connect (the #1066 behaviour)", () {
      expect(
        container.read(filterWheelPolicyProvider).value!.homeOnFirstConnect,
        isTrue,
      );
    });

    test(
      'setHomeOnFirstConnect updates the local state optimistically',
      () async {
        await container
            .read(filterWheelPolicyProvider.notifier)
            .setHomeOnFirstConnect(false);
        expect(
          container.read(filterWheelPolicyProvider).value!.homeOnFirstConnect,
          isFalse,
        );
      },
    );

    test("a failed save puts the switch back to the rig's copy", () async {
      rig.failPut.add('filter-wheel/policy');
      await expectLater(
        container
            .read(filterWheelPolicyProvider.notifier)
            .setHomeOnFirstConnect(false),
        throwsA(anything),
      );
      expect(
        container.read(filterWheelPolicyProvider).value!.homeOnFirstConnect,
        isTrue,
      );
    });

    test(
      'profile.updated for filter-wheel reloads a policy the rig changed',
      () async {
        final before = container.read(filterWheelPolicyProvider).etag;
        rig.daemonWrote('filter-wheel/policy', {
          'home_on_first_connect': true,
          'use_filter_wheel_offsets': true,
        });

        await applyProfileUpdated(container, 'filter-wheel');

        final now = container.read(filterWheelPolicyProvider);
        expect(now.etag, isNot(before));
        expect(now.value!.useFilterWheelOffsets, isTrue);
      },
    );

    test(
      'wire shape is home_on_first_connect and use_filter_wheel_offsets',
      () {
        expect(const FilterWheelPolicy(homeOnFirstConnect: false).toJson(), {
          'home_on_first_connect': false,
          'use_filter_wheel_offsets': false,
        });
      },
    );

    test('per-filter focus offsets write through the policy', () {
      expect(const FilterWheelPolicy(useFilterWheelOffsets: true).toJson()['use_filter_wheel_offsets'], isTrue);
    });
  });

  group('FilterWheelPolicyNotifier loads itself', () {
    setUp(ProfileApi.forgetOwnWrites);
    FakeSectionRig wheelRig() => FakeSectionRig({
      'filter-wheel/policy': rigDefaults['filter-wheel/policy'],
      'filter-wheel/labels': rigDefaults['filter-wheel/labels'],
    });

    test('after the reset point it loads again with no Retry', () async {
      final c = ProviderContainer(
        overrides: [profileApiProvider.overrideWithValue(wheelRig().api())],
      );
      addTearDown(c.dispose);
      c.listen(filterWheelPolicyProvider, (_, _) {});
      await settle(() => c.read(filterWheelPolicyProvider).canSave);
      expect(c.read(filterWheelPolicyProvider).canSave, isTrue);

      resetMigratedSections(c.read);
      expect(c.read(filterWheelPolicyProvider).canSave, isFalse);
      await settle(() => c.read(filterWheelPolicyProvider).canSave);
      expect(c.read(filterWheelPolicyProvider).canSave, isTrue);
    });

    test('it loads when the rig connects after it was built', () async {
      ProfileApi? current;
      final c = ProviderContainer(
        overrides: [profileApiProvider.overrideWith((ref) => current)],
      );
      addTearDown(c.dispose);
      c.listen(filterWheelPolicyProvider, (_, _) {});
      await Future<void>.delayed(const Duration(milliseconds: 50));
      expect(c.read(filterWheelPolicyProvider).canSave, isFalse);

      current = wheelRig().api();
      c.invalidate(profileApiProvider);
      await settle(() => c.read(filterWheelPolicyProvider).canSave);
      expect(c.read(filterWheelPolicyProvider).canSave, isTrue);
    });
  });
}
