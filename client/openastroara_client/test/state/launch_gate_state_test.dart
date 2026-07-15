import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/state/launch_gate_state.dart';

void main() {
  test('profile gate starts closed; pass() opens it; reset() re-arms it', () {
    final c = ProviderContainer();
    addTearDown(c.dispose);
    expect(c.read(profileGatePassedProvider), isFalse);
    c.read(profileGatePassedProvider.notifier).pass();
    expect(c.read(profileGatePassedProvider), isTrue);
    // The shell's "Launchpad" action sends the user back through the flow.
    c.read(profileGatePassedProvider.notifier).reset();
    expect(c.read(profileGatePassedProvider), isFalse);
  });

  test('offline mode starts off; enter() flips it; exit() clears it', () {
    final c = ProviderContainer();
    addTearDown(c.dispose);
    expect(c.read(offlineModeProvider), isFalse);
    c.read(offlineModeProvider.notifier).enter();
    expect(c.read(offlineModeProvider), isTrue);
    c.read(offlineModeProvider.notifier).exit();
    expect(c.read(offlineModeProvider), isFalse);
  });
}
