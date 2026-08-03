import 'dart:async';

import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/models/server.dart';
import 'package:openastroara/services/server_discovery_service.dart';

AraServer _s(String host, {int port = 5555, String? name}) =>
    AraServer(hostname: host, port: port, mdnsName: name);

void main() {
  group('ServerDiscoveryService.discover', () {
    test('sweep does NOT run when mDNS produced a result', () async {
      var sweepRan = false;
      final svc = ServerDiscoveryService(
        mdnsSource: () => Stream.fromIterable([_s('192.168.1.10', name: 'rig')]),
        sweepSource: () {
          sweepRan = true;
          return Stream.fromIterable([_s('192.168.1.10')]);
        },
      );
      final found = await svc.discover().toList();
      expect(found, hasLength(1));
      expect(found.single.hostname, '192.168.1.10');
      expect(sweepRan, isFalse,
          reason: 'a healthy mDNS answer must not trigger scan-like traffic');
    });

    test('sweep runs when mDNS finishes empty, and its results surface',
        () async {
      final svc = ServerDiscoveryService(
        mdnsSource: () => const Stream.empty(),
        sweepSource: () =>
            Stream.fromIterable([_s('192.168.8.118', name: 'rc91')]),
      );
      final found = await svc.discover().toList();
      expect(found, hasLength(1));
      expect(found.single.hostname, '192.168.8.118');
      expect(found.single.mdnsName, 'rc91');
    });

    test('sweep joins after the grace period when mDNS stays silent',
        () async {
      // An mDNS strand that never emits and never closes (wedged browse):
      // the grace timer must still bring the sweep in and its results out.
      final mdnsHang = StreamController<AraServer>();
      addTearDown(mdnsHang.close);
      final svc = ServerDiscoveryService(
        mdnsSource: () => mdnsHang.stream,
        sweepSource: () => Stream.fromIterable([_s('10.0.0.7')]),
      );
      final first = await svc.discover().first.timeout(
          ServerDiscoveryService.mdnsGracePeriod +
              const Duration(seconds: 5));
      expect(first.hostname, '10.0.0.7');
    });

    test('duplicate endpoints dedupe to one entry', () async {
      final svc = ServerDiscoveryService(
        mdnsSource: () => Stream.fromIterable([
          _s('192.168.1.10', name: 'rig'),
          _s('192.168.1.10', name: 'rig'),
          _s('192.168.1.10', port: 5556, name: 'other-port'),
        ]),
        sweepSource: () => const Stream.empty(),
      );
      final found = await svc.discover().toList();
      expect(found.map((s) => '${s.hostname}:${s.port}'),
          ['192.168.1.10:5555', '192.168.1.10:5556']);
    });

    test('stream closes once all started strategies finish', () async {
      final svc = ServerDiscoveryService(
        mdnsSource: () => const Stream.empty(),
        sweepSource: () => const Stream.empty(),
      );
      // Completes (doesn't hang) — closure bookkeeping is correct.
      await svc.discover().toList().timeout(const Duration(seconds: 5));
    });
  });
}
