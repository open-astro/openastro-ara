import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/state/settings/filter_wheel_policy_state.dart';

void main() {
  group('FilterWheelPolicyNotifier (#1075)', () {
    late ProviderContainer container;
    setUp(() => container = ProviderContainer());
    tearDown(() => container.dispose());

    test('defaults to home on first connect (the #1066 behaviour)', () {
      expect(container.read(filterWheelPolicyProvider).homeOnFirstConnect, isTrue);
    });

    test('setHomeOnFirstConnect updates the local state optimistically', () {
      container.read(filterWheelPolicyProvider.notifier).setHomeOnFirstConnect(false);
      expect(container.read(filterWheelPolicyProvider).homeOnFirstConnect, isFalse);
    });

    test('wire shape is home_on_first_connect, absent → default on', () {
      expect(FilterWheelPolicy.fromJson(const {'home_on_first_connect': false}).homeOnFirstConnect, isFalse);
      expect(FilterWheelPolicy.fromJson(const {}).homeOnFirstConnect, isTrue);
      expect(const FilterWheelPolicy(homeOnFirstConnect: false).toJson(), {'home_on_first_connect': false});
    });
  });
}
