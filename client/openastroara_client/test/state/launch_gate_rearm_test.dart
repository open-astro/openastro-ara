import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/models/server.dart';
import 'package:openastroara/state/launch_gate_state.dart';
import 'package:openastroara/state/saved_server_state.dart';

/// Test seam: the active server as a settable notifier.
class _ServerSwitch extends Notifier<AraServer?> {
  @override
  AraServer? build() => const AraServer(hostname: 'a', port: 1);
}

final _serverSwitch = NotifierProvider<_ServerSwitch, AraServer?>(_ServerSwitch.new);

/// Riverpod 3 rebuilds derived providers on the next microtask.
Future<void> settle() => Future<void>.delayed(Duration.zero);

void main() {
  const pi = (hostname: 'openastro.local', port: 5555);
  const other = (hostname: '192.168.1.123', port: 5555);

  test('#1137: a different server mid-session re-arms the gate', () {
    expect(
      shouldRearmLaunchGate(
        previous: pi,
        next: other,
        gatePassed: true,
        offline: false,
      ),
      isTrue,
    );
    expect(
      shouldRearmLaunchGate(
        previous: pi,
        next: (hostname: pi.hostname, port: 5556),
        gatePassed: true,
        offline: false,
      ),
      isTrue,
    );
  });

  test('#1137: the same server, a first server, a vanished server, a closed gate or offline mode do not', () {
    expect(
      shouldRearmLaunchGate(
        previous: pi,
        next: pi,
        gatePassed: true,
        offline: false,
      ),
      isFalse,
    );
    expect(
      shouldRearmLaunchGate(
        previous: null,
        next: pi,
        gatePassed: true,
        offline: false,
      ),
      isFalse,
    );
    expect(
      shouldRearmLaunchGate(
        previous: pi,
        next: null,
        gatePassed: true,
        offline: false,
      ),
      isFalse,
    );
    expect(
      shouldRearmLaunchGate(
        previous: pi,
        next: other,
        gatePassed: false,
        offline: false,
      ),
      isFalse,
    );
    expect(
      shouldRearmLaunchGate(
        previous: pi,
        next: other,
        gatePassed: true,
        offline: true,
      ),
      isFalse,
    );
  });

  test('#1327: the root wiring resets the passed gate when the active server changes', () async {
    final container = ProviderContainer(
      overrides: [
        activeServerProvider.overrideWith((ref) => ref.watch(_serverSwitch)),
      ],
    );
    addTearDown(container.dispose);
    // What main.dart keeps alive with a listen.
    container.listen(launchGateRearmProvider, (_, _) {});
    container.read(profileGatePassedProvider.notifier).pass();
    expect(container.read(profileGatePassedProvider), isTrue);

    container.read(_serverSwitch.notifier).state = const AraServer(hostname: 'a', port: 1);
    await settle();
    expect(container.read(profileGatePassedProvider), isTrue, reason: 'same server re-saved');

    container.read(_serverSwitch.notifier).state = const AraServer(hostname: 'b', port: 1);
    await settle();
    expect(container.read(profileGatePassedProvider), isFalse, reason: 'a different rig re-arms the gate');

    // Offline planning keeps the shell whatever the server list does.
    container.read(profileGatePassedProvider.notifier).pass();
    container.read(offlineModeProvider.notifier).enter();
    container.read(_serverSwitch.notifier).state = const AraServer(hostname: 'c', port: 1);
    await settle();
    expect(container.read(profileGatePassedProvider), isTrue);
  });
}
