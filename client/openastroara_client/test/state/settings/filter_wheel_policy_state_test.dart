import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';

import 'package:openastroara/state/profile_management_state.dart';

import '../../support/fake_section_rig.dart';

import 'package:openastroara/state/settings/filter_wheel_policy_state.dart';

void main() {
  group('FilterWheelPolicyNotifier (#1075)', () {
    late ProviderContainer container;
    setUp(() async {
      final rig = FakeSectionRig({'filter-wheel/policy': <String, Object?>{}});
      container = ProviderContainer(
        overrides: [profileApiProvider.overrideWithValue(rig.api())],
      );
      // Self-loading: wait for the rig's copy.
      container.read(filterWheelPolicyProvider);
      await settle(() => container.read(filterWheelPolicyProvider).canSave);
    });
    tearDown(() => container.dispose());

    test('defaults to home on first connect (the #1066 behaviour)', () {
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

    test('wire shape is home_on_first_connect, absent → default on', () {
      expect(FilterWheelPolicy.fromJson(const {'home_on_first_connect': false}).homeOnFirstConnect, isFalse);
      expect(FilterWheelPolicy.fromJson(const {}).homeOnFirstConnect, isTrue);
      expect(const FilterWheelPolicy(homeOnFirstConnect: false).toJson(), {
        'home_on_first_connect': false,
        'use_filter_wheel_offsets': false,
      });
    });

    test('per-filter focus offsets default off, read and write through the policy', () {
      expect(FilterWheelPolicy.fromJson(const {}).useFilterWheelOffsets, isFalse);
      expect(FilterWheelPolicy.fromJson(const {'use_filter_wheel_offsets': true}).useFilterWheelOffsets, isTrue);
      expect(const FilterWheelPolicy(useFilterWheelOffsets: true).toJson()['use_filter_wheel_offsets'], isTrue);
    });
  });
}
