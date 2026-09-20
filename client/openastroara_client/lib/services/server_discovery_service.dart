import 'dart:async';
import 'dart:convert';
import 'dart:io';
import 'dart:typed_data' show BytesBuilder;

import 'package:flutter/foundation.dart' show visibleForTesting;
import 'package:multicast_dns/multicast_dns.dart';

import '../models/server.dart';

/// Scans the local network for Ara daemons, multicast-independent:
///
/// 1. mDNS (`_openastroara._tcp.local`) per playbook §30 first-run flow +
///    §60.1 service-type registration — instant when multicast works.
/// 2. A subnet sweep probing `GET /api/v1/server/info` on port 5555 across
///    every local /24 — but ONLY as a fallback, when the mDNS browse turns
///    up nothing (finished empty, or silent past a grace period). The
///    raw-socket mDNS package is unreliable on macOS (the OS's mDNSResponder
///    owns port 5353 — `dns-sd` sees the daemon while the in-app browse
///    stays empty; site outage 2026-08-03), so discovery must not DEPEND on
///    multicast — yet scan-like probe traffic shouldn't hit every network
///    the laptop joins (hotel Wi-Fi) when mDNS is answering fine (review r2).
///
/// A sweep, once justified, is shared: the connect screen restarts discovery
/// every ~4 s, and a full /24 on a quiet Wi-Fi outlives that tick, so a new
/// pass JOINS the sweep in flight (or replays one that finished within
/// [sweepReplayWindow]) instead of cancelling it. Joining never spawns a
/// sweep: a fresh one still needs the current pass's own mDNS to come up
/// empty, and an mDNS answer drops the joined strand, so a healthy network
/// sees no further probe traffic. [resetSweepCache] (the ⟳ Rescan) forgets
/// the shared run so a daemon that went away is re-probed, not replayed.
///
/// Both paths yield numeric-IP hostnames, so the saved server never carries
/// a `.local` name that only resolves while multicast is healthy.
class ServerDiscoveryService {
  static const String serviceType = '_openastroara._tcp.local';
  static const int defaultPort = 5555;

  /// How long the mDNS browse gets to produce a first result before the
  /// sweep fallback starts alongside it.
  static const Duration mdnsGracePeriod = Duration(milliseconds: 2500);

  /// Test seams: the real strategies are network-bound, so tests inject
  /// deterministic streams here. Production callers use the default ctor.
  ServerDiscoveryService({
    this.mdnsSource,
    this.sweepSource,
    this.sweepAbandonGrace = const Duration(seconds: 2),
  });

  final Stream<AraServer> Function()? mdnsSource;
  final Stream<AraServer> Function()? sweepSource;

  /// How long a sweep keeps probing after its last listener goes away. The
  /// connect screen restarts discovery every ~4 s, and the restart detaches
  /// the old pass a moment before the new one attaches; that hop must not
  /// read as "the user left the screen", which is what really stops a sweep.
  final Duration sweepAbandonGrace;

  /// A sweep that finished this recently still counts for a new pass: its
  /// hits are replayed instead of waiting for a fresh sweep to rediscover
  /// them, and the new pass joins the sweep path at once rather than sitting
  /// out [mdnsGracePeriod] first.
  static const Duration sweepReplayWindow = Duration(seconds: 15);

  /// Run a single discovery pass. mDNS starts immediately. A sweep that is
  /// current (in flight, or finished within [sweepReplayWindow]) is joined at
  /// once for its hits; a NEW sweep is spawned only if this pass's mDNS stays
  /// empty (grace timer) or finishes empty, and an mDNS answer drops the
  /// sweep strands. Results dedupe by endpoint; the stream closes when every
  /// started strategy is done.
  Stream<AraServer> discover() {
    final controller = StreamController<AraServer>();
    final seen = <String>{};
    var pending = 1; // the mDNS strand; the sweep adds itself if started
    var sawMdnsResult = false;
    var sweepStarted = false;
    var cancelled = false;
    StreamSubscription<AraServer>? mdnsSub;
    StreamSubscription<AraServer>? sweepSub;
    StreamSubscription<AraServer>? joinSub;
    Timer? grace;

    void done() {
      if (--pending == 0 && !controller.isClosed) {
        grace?.cancel();
        unawaited(controller.close());
      }
    }

    void emit(AraServer s) {
      if (!controller.isClosed && seen.add('${s.hostname}:${s.port}')) {
        controller.add(s);
      }
    }

    void maybeStartSweep() {
      if (sweepStarted || cancelled || controller.isClosed) return;
      sweepStarted = true;
      pending++;
      // Clear the handle when the strand finishes on its own: a later
      // dropSweepStrands() must not count a finished strand as pending
      // again, or the pass closes before the mDNS record is emitted.
      sweepSub = _sharedSweep().listen(
        emit,
        onError: (Object _) {},
        onDone: () {
          sweepSub = null;
          done();
        },
      );
    }

    // mDNS answered: the sweep path is not needed on this network. Drop the
    // joined/spawned strands (their pending slots go with them) so a healthy
    // network never sees probe traffic chained from an earlier empty pass.
    void dropSweepStrands() {
      grace?.cancel();
      for (final sub in [joinSub, sweepSub]) {
        if (sub != null) {
          unawaited(sub.cancel());
          done();
        }
      }
      joinSub = null;
      sweepSub = null;
    }

    mdnsSub = (mdnsSource ?? _mdnsDiscover)().listen(
      (s) {
        if (!sawMdnsResult) {
          sawMdnsResult = true;
          dropSweepStrands();
        }
        emit(s);
      },
      onError: (Object _) {},
      onDone: () {
        // mDNS finished with nothing — the sweep is the only hope; start it
        // BEFORE done() so pending can't hit zero and close the stream first.
        if (!sawMdnsResult) maybeStartSweep();
        done();
      },
    );
    if (_sweepIsCurrent) {
      // A sweep is running or just finished: an earlier pass proved mDNS
      // empty, so take its hits now — waiting out the grace period again is
      // how the previous pass missed its own results. This only attaches or
      // replays; spawning a fresh sweep stays gated on THIS pass's mDNS.
      pending++;
      joinSub = _joinSweep().listen(
        emit,
        onError: (Object _) {},
        onDone: () {
          joinSub = null;
          done();
        },
      );
    }
    grace = Timer(mdnsGracePeriod, () {
      if (!sawMdnsResult) maybeStartSweep();
    });
    // Cancellation MUST propagate (review r4): the connect screen invalidates
    // its discovery provider every ~4 s, and without this each tick stacked a
    // fresh full sweep on top of the still-running previous ones — multiple
    // HttpClients, multiplied scan traffic, defeated batching. Cancelling the
    // inner subscriptions ends the async* generators at their next yield /
    // batch boundary and runs their finally blocks (closing the HttpClient).
    controller.onCancel = () {
      cancelled = true;
      grace?.cancel();
      unawaited(mdnsSub?.cancel());
      unawaited(joinSub?.cancel());
      unawaited(sweepSub?.cancel());
    };
    return controller.stream;
  }

  Stream<AraServer> _mdnsDiscover() async* {
    final mdns = MDnsClient();
    try {
      await mdns.start();
      await for (final PtrResourceRecord ptr in mdns.lookup<PtrResourceRecord>(
        ResourceRecordQuery.serverPointer(serviceType),
      )) {
        await for (final SrvResourceRecord srv
            in mdns.lookup<SrvResourceRecord>(
              ResourceRecordQuery.service(ptr.domainName),
            )) {
          // Resolve the SRV target to its numeric IPv4 while the multicast
          // channel is provably working (we just heard the record). Saving
          // the .local hostname instead locks the saved server to mDNS
          // resolution forever — on a flaky-multicast network the daemon
          // then reads as "down" even though it answers by IP.
          final String host;
          try {
            final a = await mdns
                .lookup<IPAddressResourceRecord>(
                  ResourceRecordQuery.addressIPv4(srv.target),
                )
                .first
                .timeout(const Duration(milliseconds: 800));
            host = a.address.address;
            // Broad on purpose: `.first` throws StateError (an Error, not
            // Exception) on an empty stream, and a dropped A-record reply is
            // the exact flaky-multicast mode this file survives — without
            // the timeout this nested await wedged EVERY later PTR/SRV
            // record and kept the merged stream from ever closing (r1).
            // ignore: avoid_catches_without_on_clauses
          } catch (_) {
            // Unresolved: do NOT emit the .local name — a saved entry keyed
            // on it reintroduces the outage this PR fixes, and it would
            // duplicate the sweep's IP entry for the same daemon (r2). The
            // sweep surfaces this host by IP instead.
            continue;
          }
          yield AraServer(
            hostname: host,
            port: srv.port,
            mdnsName: ptr.domainName,
          );
        }
      }
      // Deliberately broad: raw-socket mDNS fails in environment-specific
      // ways (port 5353 contention, sandbox denials); the sweep path is the
      // fallback, so a browse failure must stay silent, never crash the scan.
      // ignore: avoid_catches_without_on_clauses
    } catch (_) {
      // Multicast unavailable — the subnet sweep carries discovery.
    } finally {
      mdns.stop();
    }
  }

  /// The sweep in flight or most recently finished, if any. The connect
  /// screen invalidates its discovery provider every ~4 s; a full /24 sweep
  /// on a quiet Wi-Fi (silent hosts ride the connect timeout, ~1 s per batch
  /// of 64) plus the mDNS grace period takes longer than that, so cancelling
  /// the sweep on every tick meant the last batches — and any daemon living
  /// there — were NEVER probed on a platform where mDNS stays empty
  /// (Android: the raw-socket browse fails outright). Instead, a new pass
  /// attaches to the running sweep (or replays one that just finished) and
  /// the sweep only stops once nobody has listened for [sweepAbandonGrace].
  SweepRun? _sweepRun;

  /// Forget the shared sweep: an in-flight run is discarded and a finished
  /// one is no longer replayed, so the next pass probes the subnet afresh.
  /// The connect screen's ⟳ Rescan calls this — a daemon that was stopped or
  /// moved must vanish from the list, not reappear from the cache.
  void resetSweepCache() {
    _sweepRun?.discard();
    _sweepRun = null;
  }

  /// Hits of the current sweep without spawning one: the finished run's list
  /// (then done), or the live run's replay + tail.
  Stream<AraServer> _joinSweep() async* {
    final run = _sweepRun;
    if (run == null || !_sweepIsCurrent) return;
    if (run.finished) {
      // Snapshot: a ⟳ Rescan landing mid-replay clears run.found.
      yield* Stream.fromIterable(List.of(run.found));
    } else {
      yield* run.attach();
    }
  }

  bool get _sweepIsCurrent {
    final run = _sweepRun;
    if (run == null) return false;
    final at = run.finishedAt;
    return at == null || DateTime.now().difference(at) <= sweepReplayWindow;
  }

  Stream<AraServer> _sharedSweep() async* {
    // Decide and publish the run BEFORE the first yield: an async* body
    // suspends at yield*, and two live passes reading a just-finished run
    // across that suspension would each spawn a fresh sweep, the first
    // becoming an orphan that keeps probing.
    final prev = _sweepRun;
    final SweepRun run;
    var replay = const <AraServer>[];
    if (prev != null && !prev.finished) {
      run = prev;
    } else {
      if (prev != null && _sweepIsCurrent) replay = List.of(prev.found);
      final fresh = SweepRun(sweepAbandonGrace);
      _sweepRun = run = fresh;
      fresh.drive(
        sweepSource != null
            ? sweepSource!()
            : _sweepDiscover(isCancelled: () => fresh.abandoned),
      );
    }
    yield* Stream.fromIterable(replay);
    yield* run.attach();
  }

  /// Probe every host of every local /24 for an Ara daemon on [defaultPort],
  /// in bounded batches. Worst case (silent-drop hosts) a batch rides its
  /// slowest probe's timeouts, so the sweep can take several seconds on
  /// hostile networks — acceptable for a fallback that only runs when mDNS
  /// found nothing.
  Stream<AraServer> _sweepDiscover({bool Function()? isCancelled}) async* {
    final cancelledNow = isCancelled ?? () => false;
    final List<NetworkInterface> interfaces;
    try {
      interfaces = await NetworkInterface.list(
        type: InternetAddressType.IPv4,
        includeLoopback: false,
      );
      // ignore: avoid_catches_without_on_clauses
    } catch (_) {
      return; // no interface enumeration (sandbox?) — mDNS path remains
    }
    final bases = <String>{};
    final own = <String>{};
    for (final i in interfaces) {
      // Physical LANs only (review r3): sweeping a VPN/tunnel interface fires
      // ~254 unsolicited probes into a corporate network — exactly the kind
      // of traffic that trips internal scanning alerts. Name prefixes cover
      // the common tunnel drivers across macOS/Linux/Windows.
      final name = i.name.toLowerCase();
      const tunnelPrefixes = [
        'utun',
        'tun',
        'tap',
        'ppp',
        'wg',
        'zt',
        'ipsec',
        'gpd',
      ];
      if (tunnelPrefixes.any(name.startsWith)) continue;
      for (final a in i.addresses) {
        final parts = a.address.split('.');
        if (parts.length == 4) {
          bases.add(parts.sublist(0, 3).join('.'));
          own.add(a.address);
        }
      }
    }
    if (bases.isEmpty) return;
    final client = HttpClient()
      ..connectionTimeout = const Duration(milliseconds: 800);
    try {
      final hosts = <String>[
        for (final base in bases)
          for (var n = 1; n < 255; n++)
            if (!own.contains('$base.$n')) '$base.$n',
      ];
      // Batches of 64 (review r1): several interfaces (Wi-Fi + VPN) multiply
      // the /24s, and an unbounded fan-out could hit fd limits on constrained
      // stacks.
      const batch = 64;
      for (var i = 0; i < hosts.length; i += batch) {
        // A batch that finds nothing has no yield (= no generator suspension
        // point), so cancellation must be checked explicitly or a cancelled
        // pass would keep probing every remaining batch (review r4).
        if (cancelledNow()) return;
        final probes = [
          for (final h in hosts.skip(i).take(batch)) _probe(client, h),
        ];
        for (final s in await Future.wait(probes)) {
          if (s != null) yield s;
        }
      }
    } finally {
      client.close(force: true);
    }
  }

  /// GET /api/v1/server/info with tight timeouts; a parseable payload with
  /// a server_uuid is the "this really is an Ara daemon" check. Any failure
  /// (refused, timeout, non-JSON) means "not a daemon" — never an error.
  Future<AraServer?> _probe(HttpClient client, String host) async {
    try {
      final req = await client
          .getUrl(Uri.parse('http://$host:$defaultPort/api/v1/server/info'))
          .timeout(const Duration(milliseconds: 900));
      final res = await req.close().timeout(const Duration(milliseconds: 1200));
      if (res.statusCode != 200) return null;
      // Byte-capped read (review r3): probes hit arbitrary subnet hosts, and
      // a device that trickles a large body just under the time cap would
      // hold its slot ~3 s. /server/info is a few hundred bytes; anything
      // past 8 KiB is not an Ara daemon.
      const maxBodyBytes = 8192;
      final bytes = await res
          .fold<BytesBuilder>(BytesBuilder(copy: false), (b, chunk) {
            if (b.length + chunk.length > maxBodyBytes) {
              throw const FormatException('body too large for /server/info');
            }
            return b..add(chunk);
          })
          .timeout(const Duration(milliseconds: 1200));
      final json = jsonDecode(utf8.decode(bytes.takeBytes()));
      if (json is! Map<String, dynamic> || json['server_uuid'] is! String) {
        return null;
      }
      final nickname = json['nickname'];
      return AraServer(
        hostname: host,
        port: defaultPort,
        mdnsName: nickname is String && nickname.isNotEmpty ? nickname : null,
      );
      // ignore: avoid_catches_without_on_clauses
    } catch (_) {
      return null; // not an Ara daemon (or unreachable) — skip silently
    }
  }
}

/// One subnet sweep shared by every discovery pass that starts while it is
/// running. Late attachers get the hits found so far, then live ones.
///
/// Public only so its timer bookkeeping can be tested directly; production
/// code reaches it through [ServerDiscoveryService] alone.
@visibleForTesting
class SweepRun {
  SweepRun(this.abandonGrace);

  final Duration abandonGrace;
  final found = <AraServer>[];
  final _live = StreamController<AraServer>.broadcast();
  StreamSubscription<AraServer>? _drive;
  Timer? _abandonTimer;
  var _listeners = 0;
  bool abandoned = false;
  DateTime? finishedAt;

  bool get finished => finishedAt != null;

  void drive(Stream<AraServer> source) {
    _drive = source.listen(
      (s) {
        found.add(s);
        _live.add(s);
      },
      onError: (Object _) {},
      onDone: _finish,
    );
    // Armed from the start, not only on the first detach: a pass cancelled
    // between spawning the run and attaching to it would otherwise leave a
    // sweep probing the whole /24 with nobody listening and no timer to
    // stop it. The first attach cancels this; each detach re-arms it.
    _abandonTimer = Timer(abandonGrace, _abandon);
  }

  void _finish() {
    if (finished) return;
    finishedAt = DateTime.now();
    _abandonTimer?.cancel();
    unawaited(_live.close());
  }

  void _abandon() {
    if (finished || _listeners > 0) return;
    abandoned = true;
    unawaited(_drive?.cancel());
    _finish();
  }

  /// Stop probing and close every attached listener now; the run is no
  /// longer the service's current sweep, so nothing replays it.
  void discard() {
    abandoned = true;
    unawaited(_drive?.cancel());
    found.clear();
    _finish();
  }

  Stream<AraServer> attach() {
    late StreamController<AraServer> out;
    StreamSubscription<AraServer>? sub;
    out = StreamController<AraServer>(
      onListen: () {
        _listeners++;
        _abandonTimer?.cancel();
        List.of(found).forEach(out.add);
        if (finished) {
          unawaited(out.close());
          return;
        }
        sub = _live.stream.listen(
          out.add,
          onDone: () => unawaited(out.close()),
        );
      },
      onCancel: () {
        _listeners--;
        if (_listeners == 0 && !finished) {
          _abandonTimer = Timer(abandonGrace, _abandon);
        }
        return sub?.cancel();
      },
    );
    return out.stream;
  }
}
