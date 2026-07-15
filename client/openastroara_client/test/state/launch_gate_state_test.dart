import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/state/launch_gate_state.dart';

void main() {
  test('profile gate starts closed and pass() is one-way', () {
    final c = ProviderContainer();
    addTearDown(c.dispose);
    expect(c.read(profileGatePassedProvider), isFalse);
    c.read(profileGatePassedProvider.notifier).pass();
    expect(c.read(profileGatePassedProvider), isTrue);
  });

  test('offline mode starts off and enter() flips it for the session', () {
    final c = ProviderContainer();
    addTearDown(c.dispose);
    expect(c.read(offlineModeProvider), isFalse);
    c.read(offlineModeProvider.notifier).enter();
    expect(c.read(offlineModeProvider), isTrue);
  });
}
