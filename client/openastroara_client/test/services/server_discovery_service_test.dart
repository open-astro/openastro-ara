import 'dart:async';
import 'dart:convert';
import 'dart:io';

import 'package:flutter/foundation.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:multicast_dns/multicast_dns.dart';
import 'package:openastroara/models/server.dart';
import 'package:openastroara/services/server_discovery_service.dart';

AraServer _s(String host, {int port = 5555, String? name}) =>
    AraServer(hostname: host, port: port, mdnsName: name);

/// Stands in for the sandboxed multicast bind that fails on some hosts.
class _UnstartableMdns extends MDnsClient {
  @override
  Future<void> start({
    InternetAddress? listenAddress,
    NetworkInterfacesFactory? interfacesFactory,
    int mDnsPort = 5353,
    InternetAddress? mDnsAddress,
    Function? onError,
  }) async => throw const SocketException('Operation not permitted');

  @override
  void stop() {}
}

/// Starts, answers the PTR and SRV queries for one rig, and fails the A-record
/// lookup — the address-resolution half of the log coverage.
class _AddressLookupFailsMdns extends MDnsClient {
  @override
  Future<void> start({
    InternetAddress? listenAddress,
    NetworkInterfacesFactory? interfacesFactory,
    int mDnsPort = 5353,
    InternetAddress? mDnsAddress,
    Function? onError,
  }) async {}

  @override
  Stream<T> lookup<T extends ResourceRecord>(
    ResourceRecordQuery query, {
    Duration timeout = const Duration(seconds: 5),
  }) {
    if (T == PtrResourceRecord) {
      return Stream<T>.fromIterable([
        const PtrResourceRecord('_openastroara._tcp.local', 0,
            domainName: 'rig._openastroara._tcp.local') as T,
      ]);
    }
    if (T == SrvResourceRecord) {
      return Stream<T>.fromIterable([
        const SrvResourceRecord('rig._openastroara._tcp.local', 0,
            target: 'rig.local', port: 5555, priority: 0, weight: 0) as T,
      ]);
    }
    return Stream<T>.error(const SocketException('no route to multicast'));
  }

  @override
  void stop() {}
}

/// Answers PTR, SRV and A for one rig: the happy path, end to end.
class _AnsweringMdns extends MDnsClient {
  @override
  Future<void> start({
    InternetAddress? listenAddress,
    NetworkInterfacesFactory? interfacesFactory,
    int mDnsPort = 5353,
    InternetAddress? mDnsAddress,
    Function? onError,
  }) async {}

  @override
  Stream<T> lookup<T extends ResourceRecord>(
    ResourceRecordQuery query, {
    Duration timeout = const Duration(seconds: 5),
  }) {
    if (T == PtrResourceRecord) {
      return Stream<T>.fromIterable([
        const PtrResourceRecord('_openastroara._tcp.local', 0,
            domainName: 'openastro._openastroara._tcp.local') as T,
      ]);
    }
    if (T == SrvResourceRecord) {
      return Stream<T>.fromIterable([
        const SrvResourceRecord('openastro._openastroara._tcp.local', 0,
            target: 'openastro.local', port: 5555, priority: 0, weight: 0) as T,
      ]);
    }
    return Stream<T>.fromIterable([
      IPAddressResourceRecord('openastro.local', 0, address: InternetAddress('192.0.2.20')) as T,
    ]);
  }

  @override
  void stop() {}
}

/// Starts, then fails the very first query send the way macOS does when the
/// app has no Local Network permission: a synchronous SocketException with
/// errno 65 (EHOSTUNREACH) out of RawDatagramSocket.send inside lookup().
class _SendFailsMdns extends MDnsClient {
  @override
  Future<void> start({
    InternetAddress? listenAddress,
    NetworkInterfacesFactory? interfacesFactory,
    int mDnsPort = 5353,
    InternetAddress? mDnsAddress,
    Function? onError,
  }) async {}

  @override
  Stream<T> lookup<T extends ResourceRecord>(
    ResourceRecordQuery query, {
    Duration timeout = const Duration(seconds: 5),
  }) {
    throw const SocketException(
      'Send failed',
      osError: OSError('No route to host', 65),
      address: null,
      port: 5353,
    );
  }

  @override
  void stop() {}
}

/// The production failure shape (#1111): the send does not throw; dart:io
/// reports it later on the socket stream, which multicast_dns hands to the
/// onError given to start(). Optionally answers the PTR query afterwards.
class _AsyncSendErrorMdns extends MDnsClient {
  _AsyncSendErrorMdns({this.answerAfterError = false, this.errno = 65});
  final bool answerAfterError;
  final int errno;
  Function? _onError;

  @override
  Future<void> start({
    InternetAddress? listenAddress,
    NetworkInterfacesFactory? interfacesFactory,
    int mDnsPort = 5353,
    InternetAddress? mDnsAddress,
    Function? onError,
  }) async {
    _onError = onError;
  }

  @override
  Stream<T> lookup<T extends ResourceRecord>(
    ResourceRecordQuery query, {
    Duration timeout = const Duration(seconds: 5),
  }) {
    if (T == PtrResourceRecord) {
      final err = SocketException(
        'Send failed',
        osError: OSError('No route to host', errno),
        port: 5353,
      );
      // Reported on the socket stream, a microtask later, like dart:io.
      scheduleMicrotask(() {
        final cb = _onError;
        if (cb == null) throw err; // what happened before the fix
        (cb as void Function(Object, StackTrace))(err, StackTrace.current);
      });
      if (!answerAfterError) return const Stream.empty();
      return Stream<T>.fromFuture(Future.delayed(
        const Duration(milliseconds: 10),
        () => const PtrResourceRecord('_openastroara._tcp.local', 0,
            domainName: 'rig._openastroara._tcp.local') as T,
      ));
    }
    return const Stream.empty();
  }

  @override
  void stop() {}
}

/// Starts and sends fine but nothing answers: a healthy network with no
/// rig powered on.
class _SilentMdns extends MDnsClient {
  @override
  Future<void> start({
    InternetAddress? listenAddress,
    NetworkInterfacesFactory? interfacesFactory,
    int mDnsPort = 5353,
    InternetAddress? mDnsAddress,
    Function? onError,
  }) async {}

  @override
  Stream<T> lookup<T extends ResourceRecord>(
    ResourceRecordQuery query, {
    Duration timeout = const Duration(seconds: 5),
  }) => const Stream.empty();

  @override
  void stop() {}
}

void main() {
  _preferLocalSubnetTests();

  group('mDNS socket errors reported after the send (#1111 release miss)', () {
    late List<String> lines;
    setUp(() {
      lines = <String>[];
      final prior = debugPrint;
      debugPrint = (m, {wrapWidth}) => lines.add(m ?? '');
      addTearDown(() => debugPrint = prior);
    });

    test('EHOSTUNREACH is logged, flags a Local Network block, no leak',
        () async {
      final svc = ServerDiscoveryService(
        mdnsClientFactory: () => _AsyncSendErrorMdns(),
        localAddresses: () async => const ['192.168.1.2'],
        sweepSource: () => const Stream<AraServer>.empty(),
      );
      expect(svc.localNetworkBlocked.value, isFalse);
      final found = await svc.discover().toList();
      expect(found, isEmpty);
      expect(
        lines.where((l) => l.startsWith('[discovery] mDNS socket error')),
        hasLength(1),
      );
      expect(lines.single, contains('No route to host'));
      expect(svc.localNetworkBlocked.value, isTrue);
    });

    test('a later answered query clears the block', () async {
      final svc = ServerDiscoveryService(
        mdnsClientFactory: () => _AsyncSendErrorMdns(answerAfterError: true),
        localAddresses: () async => const ['192.168.1.2'],
        sweepSource: () => const Stream<AraServer>.empty(),
      );
      await svc.discover().toList();
      expect(svc.localNetworkBlocked.value, isFalse,
          reason: 'a PTR answer proves the query went out');
    });

    test('Linux EHOSTUNREACH (113) is a block too', () async {
      final svc = ServerDiscoveryService(
        mdnsClientFactory: () => _AsyncSendErrorMdns(errno: 113),
        localAddresses: () async => const ['192.168.1.2'],
        sweepSource: () => const Stream<AraServer>.empty(),
      );
      await svc.discover().toList();
      expect(svc.localNetworkBlocked.value, isTrue);
    });

    test('no IPv4 network at all is not called a block', () async {
      // Wi-Fi off / cable out: the same errno means "no route", not
      // "macOS refused"; the banner would be a wrong diagnosis.
      final svc = ServerDiscoveryService(
        mdnsClientFactory: () => _AsyncSendErrorMdns(),
        localAddresses: () async => const [],
        sweepSource: () => const Stream<AraServer>.empty(),
      );
      await svc.discover().toList();
      expect(lines.single, contains('[discovery] mDNS socket error'));
      expect(svc.localNetworkBlocked.value, isFalse);
    });

    test('a later answerless pass with no socket error clears the block',
        () async {
      // Permission granted, user taps ⟳, no rig powered on: nothing answers
      // the PTR query, so only the end-of-pass clear can take the banner
      // down.
      var pass = 0;
      final svc = ServerDiscoveryService(
        mdnsClientFactory: () => ++pass == 1
            ? _AsyncSendErrorMdns()
            : _SilentMdns(),
        localAddresses: () async => const ['192.168.1.2'],
        sweepSource: () => const Stream<AraServer>.empty(),
      );
      await svc.discover().toList();
      expect(svc.localNetworkBlocked.value, isTrue);
      await svc.discover().toList();
      expect(svc.localNetworkBlocked.value, isFalse);
    });

    test('a pass that fails before sending leaves the flag alone', () async {
      // start() throwing (port 5353 contention) never queried, so it can't
      // vouch that the block is gone.
      var pass = 0;
      final svc = ServerDiscoveryService(
        mdnsClientFactory: () => ++pass == 1
            ? _AsyncSendErrorMdns()
            : _UnstartableMdns(),
        localAddresses: () async => const ['192.168.1.2'],
        sweepSource: () => const Stream<AraServer>.empty(),
      );
      await svc.discover().toList();
      expect(svc.localNetworkBlocked.value, isTrue);
      await svc.discover().toList();
      expect(svc.localNetworkBlocked.value, isTrue);
    });

    test('an unrelated socket error is logged but not called a block',
        () async {
      final svc = ServerDiscoveryService(
        mdnsClientFactory: () => _AsyncSendErrorMdns(errno: 49), // EADDRNOTAVAIL
        localAddresses: () async => const ['192.168.1.2'],
        sweepSource: () => const Stream<AraServer>.empty(),
      );
      await svc.discover().toList();
      expect(lines.single, contains('[discovery] mDNS socket error'));
      expect(svc.localNetworkBlocked.value, isFalse);
    });
  });

  group('mDNS send failure (#1111 release-build miss)', () {
    test('a synchronous send failure is caught and logged, scan completes',
        () async {
      final lines = <String>[];
      final prior = debugPrint;
      debugPrint = (m, {wrapWidth}) => lines.add(m ?? '');
      addTearDown(() => debugPrint = prior);
      final svc = ServerDiscoveryService(
        mdnsClientFactory: _SendFailsMdns.new,
        sweepSource: () => const Stream<AraServer>.empty(),
      );
      final found = await svc.discover().toList();
      expect(found, isEmpty);
      expect(lines.where((l) => l.startsWith('[discovery]')), hasLength(1));
      expect(lines.single, contains('No route to host'));
    });

    test('cancelling the scan while mDNS is starting never leaks an error',
        () async {
      // The connect screen restarts discovery within milliseconds of the
      // first pass on a fresh launch; the first pass's send then fails
      // after its subscription is already gone.
      final lines = <String>[];
      final prior = debugPrint;
      debugPrint = (m, {wrapWidth}) => lines.add(m ?? '');
      addTearDown(() => debugPrint = prior);
      final svc = ServerDiscoveryService(
        mdnsClientFactory: _SendFailsMdns.new,
        sweepSource: () => const Stream<AraServer>.empty(),
      );
      final sub = svc.discover().listen((_) {});
      // Cancel before the async* body reaches lookup() (start() and the
      // interface enumeration are both awaited first).
      await sub.cancel();
      await Future<void>.delayed(const Duration(milliseconds: 50));
      // An unhandled async error here fails the test via the zone.
    });
  });

  group('mDNS failures are logged, not swallowed (#1111)', () {
    test('a client that cannot start is reported and the scan still ends',
        () async {
      final lines = <String>[];
      final prior = debugPrint;
      debugPrint = (m, {wrapWidth}) => lines.add(m ?? '');
      addTearDown(() => debugPrint = prior);
      final svc = ServerDiscoveryService(
        mdnsClientFactory: _UnstartableMdns.new,
        sweepSource: () => const Stream<AraServer>.empty(),
      );
      final found = await svc.discover().toList();
      expect(found, isEmpty);
      expect(
        lines.where((l) => l.startsWith('[discovery] mDNS browse failed')),
        hasLength(1),
      );
      expect(lines.single, contains('Operation not permitted'));
    });

    test('a failed A-record lookup is reported and the rig is skipped',
        () async {
      final lines = <String>[];
      final prior = debugPrint;
      debugPrint = (m, {wrapWidth}) => lines.add(m ?? '');
      addTearDown(() => debugPrint = prior);
      final svc = ServerDiscoveryService(
        mdnsClientFactory: _AddressLookupFailsMdns.new,
        sweepSource: () => const Stream<AraServer>.empty(),
      );
      final found = await svc.discover().toList();
      expect(found, isEmpty, reason: 'an unresolved .local name is never emitted');
      expect(
        lines.where((l) =>
            l.startsWith('[discovery] mDNS A-record lookup for rig.local')),
        hasLength(1),
      );
      expect(lines.single, contains('no route to multicast'));
    });
  });
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

    test('an mDNS answer stops a joined in-flight sweep', () async {
      // Pass 1 (silent mDNS) spawns a sweep that keeps running. Pass 2 joins
      // it, then mDNS answers: the sweep strand is dropped, and with no
      // listener left the run is abandoned after the grace — probing ends.
      var mdnsCalls = 0;
      var sweepCancelled = false;
      final mdnsHang = StreamController<AraServer>();
      addTearDown(mdnsHang.close);
      final sweepCtl = StreamController<AraServer>(
        onCancel: () => sweepCancelled = true,
      );
      addTearDown(sweepCtl.close);
      Stream<AraServer> lateAnswer() async* {
        await Future<void>.delayed(const Duration(milliseconds: 50));
        yield _s('10.0.0.10');
      }

      final svc = ServerDiscoveryService(
        mdnsSource: () => ++mdnsCalls == 1 ? mdnsHang.stream : lateAnswer(),
        sweepSource: () => sweepCtl.stream,
        sweepAbandonGrace: const Duration(milliseconds: 10),
      );
      final first = svc.discover().listen((_) {});
      await Future<void>.delayed(
        ServerDiscoveryService.mdnsGracePeriod +
            const Duration(milliseconds: 100),
      );
      await first.cancel();
      expect(sweepCancelled, isFalse, reason: 'pass 2 re-attaches in time');
      final got = await svc.discover().toList();
      expect(got.map((s) => s.hostname), ['10.0.0.10']);
      await Future<void>.delayed(const Duration(milliseconds: 100));
      expect(
        sweepCancelled,
        isTrue,
        reason: 'mDNS answered: the joined sweep must stop probing',
      );
    });

    test(
      'a run nobody ever attached to is abandoned after the grace',
      () async {
        // A pass cancelled in the same turn that spawned the sweep never
        // attaches, so no detach ever re-arms the abandon timer; the run must
        // stop on its own. Tested on the run itself: from the service the
        // window between spawn and attach is a single microtask.
        var sweepCancelled = false;
        final sweepCtl = StreamController<AraServer>(
          onCancel: () => sweepCancelled = true,
        );
        addTearDown(sweepCtl.close);
        final run = SweepRun(const Duration(milliseconds: 30))
          ..drive(sweepCtl.stream);
        await Future<void>.delayed(const Duration(milliseconds: 120));
        expect(
          sweepCancelled,
          isTrue,
          reason: 'an orphaned sweep must stop after abandonGrace',
        );
        expect(run.abandoned, isTrue);
        expect(run.finished, isTrue);
      },
    );

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

void _preferLocalSubnetTests() {
  group('ServerDiscoveryService.preferLocalSubnet', () {
    // The Pi advertises eth0 (house LAN) and ap0 (its own hotspot); the
    // laptop on the LAN must be offered the eth0 address, not whichever
    // A record happened to arrive first.
    test('keeps only the address sharing a /24 with a local interface', () {
      expect(
        ServerDiscoveryService.preferLocalSubnet(
          ['172.24.1.1', '192.168.1.234'],
          ['192.168.1.50'],
        ),
        ['192.168.1.234'],
      );
    });

    test('a laptop on the hotspot gets the hotspot address', () {
      expect(
        ServerDiscoveryService.preferLocalSubnet(
          ['172.24.1.1', '192.168.1.234'],
          ['172.24.1.7'],
        ),
        ['172.24.1.1'],
      );
    });

    test('no subnet match returns every candidate in received order', () {
      expect(
        ServerDiscoveryService.preferLocalSubnet(
          ['172.24.1.1', '10.0.5.2'],
          ['192.168.1.50'],
        ),
        ['172.24.1.1', '10.0.5.2'],
      );
    });

    test('no local interfaces returns every candidate', () {
      expect(ServerDiscoveryService.preferLocalSubnet(['10.0.0.1'], const []), [
        '10.0.0.1',
      ]);
    });

    test('several on-subnet candidates are all kept', () {
      expect(
        ServerDiscoveryService.preferLocalSubnet(
          ['192.168.1.2', '172.24.1.1', '192.168.1.3'],
          ['192.168.1.50', '10.9.9.9'],
        ),
        ['192.168.1.2', '192.168.1.3'],
      );
    });
  });

  // #1129: on an iPad that had found the rig, a banner claimed "iOS is
  // blocking local network access". iOS refuses raw multicast to apps without
  // Apple's multicast entitlement, so the mDNS send always failed there.
  group('iOS discovery', () {
    tearDown(() => debugDefaultTargetPlatformOverride = null);

    test('iOS never opens the multicast socket and sweeps at once', () async {
      debugDefaultTargetPlatformOverride = TargetPlatform.iOS;
      var mdnsClients = 0;
      const rig = AraServer(hostname: 'rig.test', port: 5555);
      final svc = ServerDiscoveryService(
        mdnsClientFactory: () {
          mdnsClients++;
          return _AsyncSendErrorMdns();
        },
        localAddresses: () async => const ['192.0.2.10'],
        sweepSource: () => Stream.value(rig),
      );
      final sw = Stopwatch()..start();
      expect(await svc.discover().toList(), [rig]);
      expect(mdnsClients, 0);
      expect(sw.elapsed, lessThan(ServerDiscoveryService.mdnsGracePeriod),
          reason: 'no mDNS grace wait before the sweep');
      expect(svc.localNetworkBlocked.value, isFalse);
    });

    test('blocked only when nothing on the subnet answered and the OS refused', () {
      expect(ServerDiscoveryService.sweepSaysBlocked(anyHostAnswered: false, blockedFailures: 40), isTrue);
      expect(ServerDiscoveryService.sweepSaysBlocked(anyHostAnswered: true, blockedFailures: 40), isFalse,
          reason: 'the router refusing the port proves the LAN is reachable (the rig is just off)');
      expect(ServerDiscoveryService.sweepSaysBlocked(anyHostAnswered: false, blockedFailures: 0), isFalse,
          reason: 'silent timeouts are a quiet network, not a denial');
    });
  });

  test('a rig found by the sweep clears a banner the mDNS send raised', () async {
    final svc = ServerDiscoveryService(
      mdnsClientFactory: () => _AsyncSendErrorMdns(),
      localAddresses: () async => const ['192.0.2.10'],
      sweepSource: () => Stream.value(const AraServer(hostname: 'rig.test', port: 5555)),
    );
    final found = await svc.discover().toList();
    expect(found, hasLength(1));
    expect(svc.localNetworkBlocked.value, isFalse);
  });

  // #1129 review: the sweep's "blocked" verdict rests on how a failed probe
  // is classified — a refusal proves the LAN works; an OS refusal is a block.
  group('probeErrorOutcome', () {
    SocketException err(int errno) => SocketException('x', osError: OSError('x', errno));

    test('a refused port means the host answered', () {
      for (final errno in [61, 111, 10061, 1225]) {
        expect(ServerDiscoveryService.probeErrorOutcome(err(errno)),
            (answered: true, blocked: false), reason: 'errno $errno');
      }
    });

    test('an OS refusal means blocked', () {
      for (final errno in [65, 113, 1, 13, 10013, 10065]) {
        expect(ServerDiscoveryService.probeErrorOutcome(err(errno)),
            (answered: false, blocked: true), reason: 'errno $errno');
      }
    });

    test('timeouts and other failures say neither', () {
      expect(ServerDiscoveryService.probeErrorOutcome(TimeoutException('x')),
          (answered: false, blocked: false));
      expect(ServerDiscoveryService.probeErrorOutcome(err(60)),
          (answered: false, blocked: false), reason: 'ETIMEDOUT');
      expect(ServerDiscoveryService.probeErrorOutcome(const FormatException('x')),
          (answered: false, blocked: false));
    });
  });

  // #1129 review: on a denied Mac every 4 s pass's failing mDNS send re-raised
  // the banner the found rig had just cleared, so it flickered.
  test('a failed mDNS send right after a rig answered does not re-raise the banner', () async {
    var pass = 0;
    final svc = ServerDiscoveryService(
      mdnsClientFactory: () => _AsyncSendErrorMdns(),
      localAddresses: () async => const ['192.0.2.10'],
      sweepSource: () => pass++ == 0
          ? Stream.value(const AraServer(hostname: 'rig.test', port: 5555))
          : const Stream<AraServer>.empty(),
    );
    await svc.discover().toList(); // the sweep finds the rig
    expect(svc.localNetworkBlocked.value, isFalse);
    svc.resetSweepCache();
    await svc.discover().toList(); // next pass: mDNS send fails again
    expect(svc.localNetworkBlocked.value, isFalse);
  });

  // #1129: the rig list showed `openastro._openastroara._tcp.local`.
  group('instanceName', () {
    test('strips the service suffix', () {
      expect(ServerDiscoveryService.instanceName('openastro._openastroara._tcp.local'), 'openastro');
      expect(ServerDiscoveryService.instanceName('My Rig._openastroara._tcp.local'), 'My Rig');
    });

    test('leaves the bare service type and other names alone', () {
      expect(ServerDiscoveryService.instanceName('_openastroara._tcp.local'), '_openastroara._tcp.local');
      expect(ServerDiscoveryService.instanceName('._openastroara._tcp.local'), '._openastroara._tcp.local');
      expect(ServerDiscoveryService.instanceName('openastro.local'), 'openastro.local');
    });

    test('an mDNS answer reaches the list under the rig\'s own name', () async {
      final svc = ServerDiscoveryService(
        mdnsClientFactory: _AnsweringMdns.new,
        localAddresses: () async => const ['192.0.2.10'],
        sweepSource: () => const Stream<AraServer>.empty(),
      );
      final found = await svc.discover().toList();
      expect(found.single.hostname, '192.0.2.20');
      expect(found.single.mdnsName, 'openastro');
    });
  });

  // #1129 review: the code that writes the iOS banner is the sweep's own
  // tally, not just the helpers — run the REAL sweep (host list and per-host
  // probe swapped in) under iOS and check the flag it leaves.
  group('iOS sweep verdict', () {
    tearDown(() => debugDefaultTargetPlatformOverride = null);
    final hosts = [for (var n = 1; n <= 10; n++) '192.0.2.$n'];

    ServerDiscoveryService sweeping(Future<ProbeResult> Function(String) probe) =>
        ServerDiscoveryService(
          mdnsClientFactory: () => throw StateError('iOS must not open mDNS'),
          localAddresses: () async => const ['192.0.2.100'],
          sweepHosts: () async => hosts,
          probeHost: probe,
        );

    test('every host refusing the port (rig off, LAN fine): no banner', () async {
      debugDefaultTargetPlatformOverride = TargetPlatform.iOS;
      final svc = sweeping((_) async => const ProbeResult(answered: true));
      expect(await svc.discover().toList(), isEmpty);
      expect(svc.localNetworkBlocked.value, isFalse);
    });

    test('every connect refused by the OS: banner', () async {
      debugDefaultTargetPlatformOverride = TargetPlatform.iOS;
      final svc = sweeping((_) async => const ProbeResult(blocked: true));
      expect(await svc.discover().toList(), isEmpty);
      expect(svc.localNetworkBlocked.value, isTrue);
    });

    test('a rig found: listed, and the banner clears', () async {
      debugDefaultTargetPlatformOverride = TargetPlatform.iOS;
      const rig = AraServer(hostname: '192.0.2.7', port: 5555, mdnsName: 'openastro');
      final svc = sweeping((h) async => h == '192.0.2.7'
          ? const ProbeResult(answered: true, server: rig)
          : const ProbeResult(blocked: true));
      expect(await svc.discover().toList(), [rig]);
      expect(svc.localNetworkBlocked.value, isFalse);
    });

    test('iOS reuses a recent sweep instead of re-walking the subnet every pass', () async {
      debugDefaultTargetPlatformOverride = TargetPlatform.iOS;
      var probes = 0;
      final svc = sweeping((_) async {
        probes++;
        return const ProbeResult(answered: true);
      });
      await svc.discover().toList();
      await svc.discover().toList(); // the next ~4 s pass, inside the replay window
      expect(probes, hosts.length);
    });
  });

  test('ENETUNREACH counts as an OS refusal for the sweep', () {
    for (final errno in [51, 101, 10051]) {
      expect(
          ServerDiscoveryService.probeErrorOutcome(
              SocketException('x', osError: OSError('x', errno))),
          (answered: false, blocked: true),
          reason: 'errno $errno');
    }
  });

  // The real HTTP probe against loopback: what it reports for an Ara daemon,
  // for some other HTTP server, and for a closed port.
  group('probe (real HTTP)', () {
    Future<HttpServer> serve(int status, Object body) async {
      final server = await HttpServer.bind(InternetAddress.loopbackIPv4, 0);
      server.listen((req) {
        req.response
          ..statusCode = status
          ..headers.contentType = ContentType.json
          ..write(body is String ? body : jsonEncode(body));
        req.response.close();
      });
      return server;
    }

    test('an Ara daemon is found, under its nickname', () async {
      final http = await serve(200, {'server_uuid': 'u', 'nickname': 'openastro'});
      addTearDown(() => http.close(force: true));
      final r = await ServerDiscoveryService.probeForTest('127.0.0.1', http.port);
      expect(r.server?.mdnsName, 'openastro');
      expect(r.server?.port, http.port);
      expect(r.answered, isTrue);
    });

    test('another HTTP server answered, but is not a rig', () async {
      final http = await serve(404, 'nope');
      addTearDown(() => http.close(force: true));
      final r = await ServerDiscoveryService.probeForTest('127.0.0.1', http.port);
      expect(r.server, isNull);
      expect(r.answered, isTrue, reason: 'proof the LAN is reachable');
    });

    test('a host that replied with garbage still answered', () async {
      // The reply arrives, then parsing throws: the catch must keep the
      // "answered" it already saw, or a LAN full of non-rig web servers
      // would read as blocked.
      final http = await serve(200, 'not json at all');
      addTearDown(() => http.close(force: true));
      final r = await ServerDiscoveryService.probeForTest('127.0.0.1', http.port);
      expect(r.server, isNull);
      expect(r.answered, isTrue);
    });

    test('a closed port is a refusal: answered, not blocked', () async {
      final probe = await ServerSocket.bind(InternetAddress.loopbackIPv4, 0);
      final port = probe.port;
      await probe.close();
      final r = await ServerDiscoveryService.probeForTest('127.0.0.1', port);
      expect(r.server, isNull);
      expect(r.answered, isTrue);
      expect(r.blocked, isFalse);
    },
        // Windows retries a refused SYN for ~2 s before reporting it, past the
        // probe's 800 ms connect timeout, so a closed port reads as a timeout
        // there. The refusal verdict only feeds the iOS banner, so nothing
        // depends on it on Windows (CI windows-latest, #1129).
        skip: Platform.isWindows
            ? 'Windows reports a refused connect only after ~2 s of SYN retries'
            : false);
  });
}
