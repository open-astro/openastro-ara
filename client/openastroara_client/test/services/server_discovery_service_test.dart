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
        mdnsSource: () =>
            Stream.fromIterable([_s('192.168.1.10', name: 'rig')]),
        sweepSource: () {
          sweepRan = true;
          return Stream.fromIterable([_s('192.168.1.10')]);
        },
      );
      final found = await svc.discover().toList();
      expect(found, hasLength(1));
      expect(found.single.hostname, '192.168.1.10');
      expect(
        sweepRan,
        isFalse,
        reason: 'a healthy mDNS answer must not trigger scan-like traffic',
      );
    });

    test(
      'sweep runs when mDNS finishes empty, and its results surface',
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
      },
    );

    test('sweep joins after the grace period when mDNS stays silent', () async {
      // An mDNS strand that never emits and never closes (wedged browse):
      // the grace timer must still bring the sweep in and its results out.
      final mdnsHang = StreamController<AraServer>();
      addTearDown(mdnsHang.close);
      final svc = ServerDiscoveryService(
        mdnsSource: () => mdnsHang.stream,
        sweepSource: () => Stream.fromIterable([_s('10.0.0.7')]),
      );
      final first = await svc.discover().first.timeout(
        ServerDiscoveryService.mdnsGracePeriod + const Duration(seconds: 5),
      );
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
      expect(found.map((s) => '${s.hostname}:${s.port}'), [
        '192.168.1.10:5555',
        '192.168.1.10:5556',
      ]);
    });

    test('cancelling discover() cancels the underlying strategies', () async {
      // The connect screen invalidates its provider every ~4 s; without
      // propagation each tick stacked a fresh sweep on the running ones.
      var mdnsCancelled = false;
      var sweepCancelled = false;
      final mdnsCtl = StreamController<AraServer>(
        onCancel: () => mdnsCancelled = true,
      );
      final sweepCtl = StreamController<AraServer>(
        onCancel: () => sweepCancelled = true,
      );
      addTearDown(mdnsCtl.close);
      addTearDown(sweepCtl.close);
      final svc = ServerDiscoveryService(
        mdnsSource: () {
          // Close mdns empty immediately so the sweep starts.
          unawaited(mdnsCtl.close());
          return mdnsCtl.stream;
        },
        sweepSource: () => sweepCtl.stream,
        sweepAbandonGrace: const Duration(milliseconds: 10),
      );
      final sub = svc.discover().listen((_) {});
      await Future<void>.delayed(const Duration(milliseconds: 50));
      await sub.cancel();
      await Future<void>.delayed(const Duration(milliseconds: 50));
      expect(mdnsCancelled || mdnsCtl.isClosed, isTrue);
      expect(
        sweepCancelled,
        isTrue,
        reason: 'an in-flight sweep must stop when the listener goes away',
      );
    });

    test(
      'a restarted pass keeps the in-flight sweep and gets its later hits',
      () async {
        // The connect screen restarts discovery every ~4 s. On Android (mDNS
        // never answers) a /24 sweep outlives that tick, so cancelling it per
        // tick meant the daemon's batch was never reached. The restart must
        // attach to the running sweep instead.
        var sweepStarts = 0;
        final sweepCtl = StreamController<AraServer>();
        addTearDown(sweepCtl.close);
        final svc = ServerDiscoveryService(
          mdnsSource: () => const Stream.empty(),
          sweepSource: () {
            sweepStarts++;
            return sweepCtl.stream;
          },
        );
        final first = svc.discover().listen((_) {});
        await Future<void>.delayed(const Duration(milliseconds: 20));
        sweepCtl.add(const AraServer(hostname: '10.0.0.5', port: 5555));
        await Future<void>.delayed(const Duration(milliseconds: 20));
        await first.cancel();
        final got = <AraServer>[];
        final second = svc.discover().listen(got.add);
        await Future<void>.delayed(const Duration(milliseconds: 20));
        sweepCtl.add(const AraServer(hostname: '10.0.0.235', port: 5555));
        await Future<void>.delayed(const Duration(milliseconds: 20));
        await second.cancel();
        expect(
          sweepStarts,
          1,
          reason: 'the restart must not start a new sweep',
        );
        expect(got.map((s) => s.hostname), [
          '10.0.0.5',
          '10.0.0.235',
        ], reason: 'earlier hits replay, later ones arrive live');
      },
    );

    test(
      'a restarted pass joins a current sweep without waiting out the grace',
      () async {
        // Both passes see a wedged mDNS browse (never emits, never closes), so
        // nothing but the grace-skip branch can bring the sweep in early on
        // pass 2. Without it, pass 2 would sit out mdnsGracePeriod again and
        // the replayed hit would arrive ~2.5 s late.
        final mdnsHang = StreamController<AraServer>.broadcast();
        addTearDown(mdnsHang.close);
        final sweepCtl = StreamController<AraServer>();
        addTearDown(sweepCtl.close);
        final svc = ServerDiscoveryService(
          mdnsSource: () => mdnsHang.stream,
          sweepSource: () => sweepCtl.stream,
        );
        final first = svc.discover().listen((_) {});
        await Future<void>.delayed(
          ServerDiscoveryService.mdnsGracePeriod +
              const Duration(milliseconds: 100),
        );
        sweepCtl.add(_s('10.0.0.235'));
        await Future<void>.delayed(const Duration(milliseconds: 20));
        await first.cancel();
        final sw = Stopwatch()..start();
        final got = await svc.discover().first.timeout(
          const Duration(milliseconds: 500),
        );
        expect(got.hostname, '10.0.0.235');
        expect(sw.elapsed, lessThan(ServerDiscoveryService.mdnsGracePeriod));
      },
    );

    test(
      'rescan drops the cached sweep so a stale entry is re-probed',
      () async {
        // The connect screen's ⟳ clears its list and re-runs discovery; if the
        // service replayed the cached run, a daemon that just went away would
        // reappear within milliseconds without anyone probing it.
        var sweepStarts = 0;
        final svc = ServerDiscoveryService(
          mdnsSource: () => const Stream.empty(),
          sweepSource: () {
            sweepStarts++;
            return sweepStarts == 1
                ? Stream.value(_s('10.0.0.235'))
                : const Stream<AraServer>.empty();
          },
        );
        expect((await svc.discover().toList()).map((s) => s.hostname), [
          '10.0.0.235',
        ]);
        svc.resetSweepCache();
        expect(
          await svc.discover().toList(),
          isEmpty,
          reason: 'a rescan must re-probe, not replay the cached hit',
        );
        expect(sweepStarts, 2);
      },
    );

    test('a pass whose mDNS answers neither spawns nor chains a sweep', () async {
      // Pass 1: the browse is silent, so the sweep runs (and finishes).
      // Passes 2 and 3: mDNS answers at once. Joining a current sweep is fine,
      // but no NEW sweep may be spawned while mDNS is healthy — that is the
      // "no scan-like traffic on every network" contract (review r2).
      var mdnsCalls = 0;
      var sweepStarts = 0;
      final mdnsHang = StreamController<AraServer>();
      addTearDown(mdnsHang.close);
      // The healthy browse answers after a real-world delay, i.e. AFTER the
      // join strand has replayed the finished run and completed: the pass
      // must still emit the mDNS record (a strand that already finished is
      // not dropped a second time).
      Stream<AraServer> lateAnswer() async* {
        await Future<void>.delayed(const Duration(milliseconds: 50));
        yield _s('10.0.0.10');
      }

      final svc = ServerDiscoveryService(
        mdnsSource: () => ++mdnsCalls == 1 ? mdnsHang.stream : lateAnswer(),
        sweepSource: () {
          sweepStarts++;
          return const Stream<AraServer>.empty();
        },
      );
      final first = svc.discover().listen((_) {});
      await Future<void>.delayed(
        ServerDiscoveryService.mdnsGracePeriod +
            const Duration(milliseconds: 100),
      );
      await first.cancel();
      expect(sweepStarts, 1);
      for (var pass = 2; pass <= 3; pass++) {
        final got = await svc.discover().toList();
        expect(got.map((s) => s.hostname), ['10.0.0.10'], reason: 'pass $pass');
        expect(
          sweepStarts,
          1,
          reason: 'pass $pass: mDNS answered, so no fresh sweep',
        );
      }
    });

    test(
      'a sweep that just finished replays its hits to the next pass',
      () async {
        // Real timeline on the tablet: the hit landed after the tick detached
        // the pass and before the next pass attached, and a finished run was
        // thrown away — so nothing ever reached the screen.
        var sweepStarts = 0;
        final svc = ServerDiscoveryService(
          mdnsSource: () => const Stream.empty(),
          sweepSource: () {
            sweepStarts++;
            return Stream.value(
              const AraServer(hostname: '10.0.0.235', port: 5555),
            );
          },
        );
        final first = svc.discover().listen((_) {});
        await Future<void>.delayed(const Duration(milliseconds: 20));
        await first.cancel();
        await Future<void>.delayed(const Duration(milliseconds: 20));
        final got = await svc.discover().toList();
        expect(got.map((s) => s.hostname), ['10.0.0.235']);
        expect(
          sweepStarts,
          2,
          reason: 'a finished run is replayed, then a fresh sweep follows',
        );
      },
    );

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
