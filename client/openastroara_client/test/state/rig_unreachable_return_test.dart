import 'package:fake_async/fake_async.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/state/launch_gate_state.dart';
import 'package:openastroara/state/simple_mode_state.dart';
import 'package:openastroara/state/ws/ws_providers.dart';

/// Test seams: the link and the shell choice as settable notifiers.
class _Flag extends Notifier<bool> {
  _Flag(this.initial);
  final bool initial;
  @override
  bool build() => initial;
  void set(bool value) => state = value;
}

final _link = NotifierProvider<_Flag, bool>(() => _Flag(true));
final _simple = NotifierProvider<_Flag, bool>(() => _Flag(false));

ProviderContainer _container() {
  final container = ProviderContainer(
    overrides: [
      serverLinkUpProvider.overrideWith((ref) => ref.watch(_link)),
      simpleModeActiveProvider.overrideWith((ref) => ref.watch(_simple)),
    ],
  );
  // What main.dart keeps alive with a listen.
  container.listen(rigUnreachableReturnProvider, (_, _) {});
  container.read(profileGatePassedProvider.notifier).pass();
  return container;
}

void main() {
  test('#1570: the full UI goes back to the profile screen after 15 s without the rig', () {
    fakeAsync((async) {
      final container = _container();
      addTearDown(container.dispose);

      container.read(_link.notifier).set(false);
      async.elapse(const Duration(seconds: 14));
      expect(container.read(profileGatePassedProvider), isTrue, reason: 'still inside the grace');

      async.elapse(const Duration(seconds: 2));
      expect(container.read(profileGatePassedProvider), isFalse);
    });
  });

  test('#1570: a link back inside the grace keeps the shell, and the next drop starts a fresh 15 s', () {
    fakeAsync((async) {
      final container = _container();
      addTearDown(container.dispose);

      container.read(_link.notifier).set(false);
      async.elapse(const Duration(seconds: 10));
      container.read(_link.notifier).set(true);
      async.elapse(const Duration(seconds: 1));
      container.read(_link.notifier).set(false);
      async.elapse(const Duration(seconds: 10));
      expect(container.read(profileGatePassedProvider), isTrue, reason: 'a short drop does not add up');

      async.elapse(const Duration(seconds: 6));
      expect(container.read(profileGatePassedProvider), isFalse);
    });
  });

  test('#1570: Simple Mode and offline planning stay in the shell', () {
    fakeAsync((async) {
      final container = _container();
      addTearDown(container.dispose);

      container.read(_simple.notifier).set(true);
      container.read(_link.notifier).set(false);
      async.elapse(const Duration(seconds: 30));
      expect(container.read(profileGatePassedProvider), isTrue, reason: 'Simple Mode shows its Connect card instead (#1559)');

      container.read(_simple.notifier).set(false);
      container.read(offlineModeProvider.notifier).enter();
      async.elapse(const Duration(seconds: 30));
      expect(container.read(profileGatePassedProvider), isTrue, reason: 'offline planning has no rig to lose');
    });
  });
}
