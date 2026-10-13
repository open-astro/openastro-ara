import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/state/launch_gate_state.dart';

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
}
