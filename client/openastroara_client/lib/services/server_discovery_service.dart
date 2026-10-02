import 'dart:async';
import 'dart:convert';
import 'dart:io';
import 'dart:typed_data' show BytesBuilder;

import 'package:flutter/foundation.dart'
    show
        TargetPlatform,
        ValueListenable,
        ValueNotifier,
        debugPrint,
        defaultTargetPlatform,
        visibleForTesting;
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
/// Both paths yield numeric-IP hostnames, so the session's chosen rig never
/// carries a `.local` name that only resolves while multicast is healthy.
class ServerDiscoveryService {
  static const String serviceType = '_openastroara._tcp.local';
  static const int defaultPort = 5555;

  /// How long the mDNS browse gets to produce a first result before the
  /// sweep fallback starts alongside it.
  static const Duration mdnsGracePeriod = Duration(milliseconds: 2500);

  /// A-record collection per rig: close after this long with no new record
  /// (the same tolerance for the first record as the old `.first.timeout`),
  /// and never run longer than [_aRecordDeadline] in total.
  static const Duration _aRecordIdleWindow = Duration(milliseconds: 800);
  static const Duration _aRecordDeadline = Duration(milliseconds: 1500);

  /// Test seams: the real strategies are network-bound, so tests inject
  /// deterministic streams here. Production callers use the default ctor.
  ServerDiscoveryService({
    this.mdnsSource,
    this.sweepSource,
    this.sweepAbandonGrace = const Duration(seconds: 2),
    MDnsClient Function()? mdnsClientFactory,
    Future<List<String>> Function()? localAddresses,
    @visibleForTesting this.sweepHosts,
    @visibleForTesting this.probeHost,
  }) : _mdnsClientFactory = mdnsClientFactory ?? _platformMdnsClient,
       _localAddresses = localAddresses ?? _localIPv4Addresses;

  /// Test seams for the REAL sweep (unlike [sweepSource], which replaces it
  /// whole): the hosts it walks, and what probing one host finds. With these
  /// a test runs the sweep's own tally — hits, hosts that answered, OS
  /// refusals — and the iOS banner verdict it writes (#1129 review).
  final Future<List<String>> Function()? sweepHosts;
  final Future<ProbeResult> Function(String host)? probeHost;

  /// `multicast_dns` binds with `reusePort: true`, which `dart:io` rejects on
  /// Android, so on Android the browse died in `start()` every pass and logged
  /// it every tick (#1129). Same client, minus that one option there.
  static MDnsClient _platformMdnsClient() =>
      MDnsClient(rawDatagramSocketFactory: productionDatagramBind());

  /// The bind production uses on the platform this runs on.
  /// defaultTargetPlatform rather than dart:io Platform so a test can pin the
  /// wiring itself, not just the helper (#1129 review: a hard-coded flag here
  /// passed every helper test while Android kept the broken bind).
  @visibleForTesting
  static RawDatagramSocketFactory productionDatagramBind({
    RawDatagramSocketFactory bind = RawDatagramSocket.bind,
  }) =>
      platformDatagramBind(defaultTargetPlatform == TargetPlatform.android, bind: bind);

  /// The rig's own name from a PTR answer: `openastro._openastroara._tcp.local`
  /// → `openastro` (the list showed the whole service name, #1129).
  static String instanceName(String domainName) {
    const suffix = '.$serviceType';
    return domainName.endsWith(suffix) && domainName.length > suffix.length
        ? domainName.substring(0, domainName.length - suffix.length)
        : domainName;
  }

  /// The bind `MDnsClient` gets: `RawDatagramSocket.bind`, except that
  /// `reusePort` is dropped when [isAndroid]. Public for the test.
  static RawDatagramSocketFactory platformDatagramBind(
    bool isAndroid, {
    RawDatagramSocketFactory bind = RawDatagramSocket.bind,
  }) =>
      (dynamic host, int port,
              {bool reuseAddress = true, bool reusePort = false, int ttl = 1}) =>
          bind(host, port,
              reuseAddress: reuseAddress,
              reusePort: isAndroid ? false : reusePort,
              ttl: ttl);

  /// Test seam for the local IPv4 enumeration (production uses
  /// `NetworkInterface.list`).
  final Future<List<String>> Function() _localAddresses;

  final Stream<AraServer> Function()? mdnsSource;
  final Stream<AraServer> Function()? sweepSource;

  /// Test seam for the real mDNS path: a client whose `start()` throws
  /// stands in for the sandboxed-socket failure the log line below exists
  /// to make visible.
  final MDnsClient Function() _mdnsClientFactory;

  /// True while the OS refuses this app's multicast queries (#1111). On
  /// macOS that is the Local Network privacy setting: a denied or
  /// unanswered prompt makes every mDNS send fail with EHOSTUNREACH, so the
  /// rig never appears although `dns-sd -B` on the same machine sees it.
  /// The connect screen shows the fix (System Settings → Privacy & Security
  /// → Local Network) while this is true; cleared by the next answered
  /// query.
  ValueListenable<bool> get localNetworkBlocked => _localNetworkBlocked;
  final ValueNotifier<bool> _localNetworkBlocked = ValueNotifier(false);

  /// iOS refuses raw multicast sockets to every app that lacks Apple's
  /// `com.apple.developer.networking.multicast` entitlement — even with
  /// Local Network allowed (`NSBonjourServices` only covers Apple's own
  /// Bonjour API, which `multicast_dns` does not use). The send always fails
  /// there, so the browse is skipped and the subnet sweep runs at once; its
  /// failure was also what raised a false "iOS is blocking local network
  /// access" banner while the sweep had found the rig (#1129).
  @visibleForTesting
  static bool get rawMulticastAllowed => defaultTargetPlatform != TargetPlatform.iOS;

  /// errno values for "the remote host answered and refused the port":
  /// ECONNREFUSED on macOS/iOS (61), Linux/Android (111) and Windows (10061).
  /// Proof that unicast to the LAN works, whatever runs on that host.
  static const _refusedErrnos = {61, 111, 10061};

  /// ENETUNREACH (51 macOS/iOS, 101 Linux/Android, 10051 Windows): a unicast
  /// connect to a host on our own subnet that can't even be routed is the OS
  /// refusing it. Counted for the sweep only — an mDNS send can fail this way
  /// on a machine with no multicast route, which isn't a permission problem.
  static const _sweepOnlyBlockedErrnos = {51, 101, 10051};

  /// What a failed probe says about the host: refused the port (it answered,
  /// so the LAN is reachable), or the OS refused the connection (blocked).
  /// Timeouts and other failures say neither.
  @visibleForTesting
  static ({bool answered, bool blocked}) probeErrorOutcome(Object error) {
    if (error is SocketException) {
      final errno = error.osError?.errorCode;
      if (_refusedErrnos.contains(errno)) return (answered: true, blocked: false);
      if (_isBlockedSend(error) || _sweepOnlyBlockedErrnos.contains(errno)) {
        return (answered: false, blocked: true);
      }
    }
    return (answered: false, blocked: false);
  }

  /// The sweep's verdict on Local Network access where mDNS can't give one
  /// (iOS): blocked only if no host on the subnet answered at all — not the
  /// rig, not even the router refusing the port — and some probe came back
  /// with an OS refusal. A rig that is simply off leaves the router answering.
  @visibleForTesting
  static bool sweepSaysBlocked({required bool anyHostAnswered, required int blockedFailures}) =>
      !anyHostAnswered && blockedFailures > 0;

  /// errno values a blocked multicast send comes back with: EHOSTUNREACH
  /// (65 on macOS/BSD, 113 on Linux; macOS Local Network denial), EPERM (1)
  /// and EACCES (13) for a sandbox or firewall, and their WinSock
  /// counterparts WSAEACCES (10013) and WSAEHOSTUNREACH (10065).
  static const _blockedErrnos = {65, 113, 1, 13, 10013, 10065};

  /// Whether [error] is the OS refusing our multicast send.
  static bool _isBlockedSend(Object error) =>
      error is SocketException &&
      _blockedErrnos.contains(error.osError?.errorCode);

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
      // A rig answered, by whatever path: the local network is reachable.
      _localNetworkBlocked.value = false;
      _lastRigSeen = DateTime.now();
      if (!controller.isClosed && seen.add('${s.hostname}:${s.port}')) {
        controller.add(s);
      }
    }

    void maybeStartSweep() {
      if (sweepStarted || cancelled || controller.isClosed) return;
      // A pass already joined to a sweep still IN FLIGHT has nothing to gain
      // from a second attach to the same run: the join strand carries its
      // hits. A join that only replayed a finished run must not block the
      // fresh sweep this pass is entitled to.
      if (joinSub != null && !(_sweepRun?.finished ?? true)) return;
      // iOS has no mDNS answer to cut sweeping short, so each ~4 s pass would
      // start a fresh 254-host sweep for as long as the scan screen is open.
      // A pass that joined a recent sweep (in flight or finished within
      // [sweepReplayWindow]) already carries its hits; don't start another.
      if (!rawMulticastAllowed && joinSub != null) return;
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

    mdnsSub = (mdnsSource ??
            (rawMulticastAllowed ? _mdnsDiscover : () => const Stream<AraServer>.empty()))()
        .listen(
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
    final mdns = _mdnsClientFactory();
    // Pass-local, not fields: the connect screen restarts discovery every
    // 4 s and passes overlap, so one pass's error must not be read as
    // another's. `hasNetwork` decides whether EHOSTUNREACH means "blocked"
    // or just "no Wi-Fi, cable out".
    var sawSocketError = false;
    var hasNetwork = false;
    // Only a pass that actually queried can vouch that the block is gone;
    // one that died in start() (port 5353 contention, say) says nothing.
    var sent = false;
    // Socket-level errors from the mDNS client. `dart:io` does NOT throw on
    // a datagram send failure; it reports it asynchronously on the socket's
    // event stream, which `multicast_dns` forwards only to the `onError`
    // given to `start()`. Without this hook the failure was an unhandled
    // async error and the browse simply produced nothing. The library
    // attaches the hook to its IPv4 socket, the only socket it sends on for
    // the default IPv4 mDNS address this service uses.
    void onSocketError(Object error, StackTrace stack) {
      debugPrint('[discovery] mDNS socket error: $error');
      sawSocketError = true;
      final rigJustAnswered = _lastRigSeen != null &&
          DateTime.now().difference(_lastRigSeen!) < _rigSeenVouchesFor;
      if (hasNetwork && _isBlockedSend(error) && !rigJustAnswered) {
        _localNetworkBlocked.value = true;
      }
    }

    try {
      await mdns.start(onError: onSocketError);
      // One interface enumeration per pass, not one per rig resolved.
      final local = await _localAddresses();
      hasNetwork = local.isNotEmpty;
      sent = true; // the PTR query below is the first send
      await for (final PtrResourceRecord ptr in mdns.lookup<PtrResourceRecord>(
        ResourceRecordQuery.serverPointer(serviceType),
      )) {
        // An answer means the query went out and came back: not blocked.
        _localNetworkBlocked.value = false;
        await for (final SrvResourceRecord srv
            in mdns.lookup<SrvResourceRecord>(
              ResourceRecordQuery.service(ptr.domainName),
            )) {
          // Resolve the SRV target to its numeric IPv4 while the multicast
          // channel is provably working (we just heard the record). Saving
          // the .local hostname instead ties the chosen rig to mDNS
          // resolution forever — on a flaky-multicast network the daemon
          // then reads as "down" even though it answers by IP.
          //
          // The daemon advertises EVERY address it holds, and an SBC running
          // its own hotspot has two (eth0 on the house LAN + ap0 on the
          // hotspot). Taking the first A record to arrive picked whichever
          // the Pi listed first — the unreachable hotspot address when the
          // laptop is on the LAN. Collect the whole answer set and prefer the
          // address that shares a /24 with a local interface.
          final candidates = <String>[];
          try {
            // The lookup stream only closes on the library's own long
            // timeout; an idle window gathers the burst of A records that
            // answer one query without wedging later PTR/SRV records (r1:
            // without a timeout this nested await wedged EVERY later record
            // and kept the stream from closing). The idle window is a floor
            // on every resolution and rigs resolve serially, so an overall
            // cap keeps several rigs inside [mdnsGracePeriod].
            final sub = mdns
                .lookup<IPAddressResourceRecord>(
                  ResourceRecordQuery.addressIPv4(srv.target),
                )
                .map((a) => a.address.address)
                .timeout(
                  _aRecordIdleWindow,
                  onTimeout: (sink) => sink.close(),
                )
                .listen(candidates.add);
            // A cancellable Timer, not Future.delayed: a deadline that
            // outlives the subscription would leave a timer pending on
            // every resolution (and trip FakeAsync once this path is
            // under test).
            final deadline = Completer<void>();
            final timer = Timer(_aRecordDeadline, deadline.complete);
            try {
              await Future.any<void>([sub.asFuture<void>(), deadline.future]);
            } finally {
              timer.cancel();
              await sub.cancel();
            }
            // Broad on purpose: a dropped A-record reply is the exact
            // flaky-multicast mode this file survives.
            // ignore: avoid_catches_without_on_clauses
          } catch (e) {
            debugPrint('[discovery] mDNS A-record lookup for ${srv.target} '
                'failed: $e');
            continue;
          }
          if (candidates.isEmpty) {
            // Unresolved: do NOT emit the .local name — a saved entry keyed
            // on it reintroduces the outage this PR fixes, and it would
            // duplicate the sweep's IP entry for the same daemon (r2). The
            // sweep surfaces this host by IP instead.
            continue;
          }
          for (final host in preferLocalSubnet(candidates, local)) {
            yield AraServer(
              hostname: host,
              port: srv.port,
              mdnsName: instanceName(ptr.domainName),
            );
          }
        }
      }
      // Deliberately broad: raw-socket mDNS fails in environment-specific
      // ways (port 5353 contention, sandbox denials); the sweep path is the
      // fallback, so a browse failure must never crash the scan. It is no
      // longer SILENT, though: a release build that never lists a rig that
      // `dns-sd -B` sees on the same machine (#1111) left nothing to read.
      // ignore: avoid_catches_without_on_clauses
    } catch (e) {
      debugPrint('[discovery] mDNS browse failed, sweep carries discovery: $e');
    } finally {
      mdns.stop();
      // A pass that sent without a socket error means the block is gone
      // (permission granted, then ⟳), even when no rig answered. Socket
      // errors are reported a microtask after the send, so give them that
      // turn before deciding.
      await Future<void>.delayed(Duration.zero);
      if (sent && !sawSocketError) _localNetworkBlocked.value = false;
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

  /// When a rig last answered, by any path. A failed mDNS send right after a
  /// rig answered says nothing about access, so it doesn't re-raise the
  /// banner (#1129: it flickered on a denied Mac while the rig was listed).
  DateTime? _lastRigSeen;
  static const _rigSeenVouchesFor = Duration(seconds: 30);

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

  /// Keep the addresses one daemon advertised that a local interface can reach.
  ///
  /// Dart exposes no netmask, so "same subnet" means the same /24 as a local
  /// interface — the assumption the sweep already makes. When one or more
  /// candidates match, only those are returned (the rest are that rig's
  /// other networks and would show as dead rows). When none match — a /16
  /// LAN, a routed segment — every candidate is returned in the order
  /// received so the user can still pick.
  @visibleForTesting
  static List<String> preferLocalSubnet(
    List<String> candidates,
    Iterable<String> localAddresses,
  ) {
    final localBases = {
      for (final a in localAddresses) _slash24(a),
    }..remove(null);
    final onSubnet = [
      for (final c in candidates)
        if (localBases.contains(_slash24(c))) c,
    ];
    return onSubnet.isNotEmpty ? onSubnet : List.of(candidates);
  }

  static String? _slash24(String address) {
    final parts = address.split('.');
    return parts.length == 4 ? parts.sublist(0, 3).join('.') : null;
  }

  /// Physical LANs only (review r3): a VPN/tunnel interface's subnet is not
  /// one a rig on the desk is reachable through, and sweeping it fires ~254
  /// unsolicited probes into a corporate network — exactly the kind of
  /// traffic that trips internal scanning alerts. Name prefixes cover the
  /// common tunnel drivers across macOS/Linux/Windows.
  static const List<String> _tunnelPrefixes = [
    'utun',
    'tun',
    'tap',
    'ppp',
    'wg',
    'zt',
    'ipsec',
    'gpd',
  ];

  static bool _isTunnel(NetworkInterface i) {
    final name = i.name.toLowerCase();
    return _tunnelPrefixes.any(name.startsWith);
  }

  /// IPv4 addresses of the local non-tunnel interfaces; empty when the
  /// enumeration is unavailable (sandbox?).
  static Future<List<String>> _localIPv4Addresses() async {
    try {
      final interfaces = await NetworkInterface.list(
        type: InternetAddressType.IPv4,
        includeLoopback: false,
      );
      return [
        for (final i in interfaces)
          if (!_isTunnel(i))
            for (final a in i.addresses) a.address,
      ];
      // ignore: avoid_catches_without_on_clauses
    } catch (_) {
      return const [];
    }
  }

  /// Probe every host of every local /24 for an Ara daemon on [defaultPort],
  /// in bounded batches. Worst case (silent-drop hosts) a batch rides its
  /// slowest probe's timeouts, so the sweep can take several seconds on
  /// hostile networks — acceptable for a fallback that only runs when mDNS
  /// found nothing.
  Stream<AraServer> _sweepDiscover({bool Function()? isCancelled}) async* {
    final cancelledNow = isCancelled ?? () => false;
    final hosts = await (sweepHosts ?? _subnetHosts)();
    if (hosts.isEmpty) return;
    final client = HttpClient()
      ..connectionTimeout = const Duration(milliseconds: 800);
    final probe = probeHost ?? (String h) => _probe(client, h);
    try {
      // Batches of 64 (review r1): several interfaces (Wi-Fi + VPN) multiply
      // the /24s, and an unbounded fan-out could hit fd limits on constrained
      // stacks.
      const batch = 64;
      var hits = 0;
      var anyHostAnswered = false;
      var blockedFailures = 0;
      for (var i = 0; i < hosts.length; i += batch) {
        // A batch that finds nothing has no yield (= no generator suspension
        // point), so cancellation must be checked explicitly or a cancelled
        // pass would keep probing every remaining batch (review r4).
        if (cancelledNow()) return;
        final probes = [
          for (final h in hosts.skip(i).take(batch)) probe(h),
        ];
        for (final r in await Future.wait(probes)) {
          anyHostAnswered |= r.answered;
          if (r.blocked) blockedFailures++;
          if (r.server != null) {
            hits++;
            yield r.server!;
          }
        }
      }
      // Where mDNS can't report a blocked network (iOS), a finished sweep
      // does. Elsewhere the mDNS path owns the flag.
      if (!rawMulticastAllowed && hits == 0) {
        _localNetworkBlocked.value = sweepSaysBlocked(
            anyHostAnswered: anyHostAnswered, blockedFailures: blockedFailures);
      }
    } finally {
      client.close(force: true);
    }
  }

  /// Every other host of every local (non-tunnel) /24; empty when interface
  /// enumeration is unavailable (sandbox?).
  static Future<List<String>> _subnetHosts() async {
    final List<NetworkInterface> interfaces;
    try {
      interfaces = await NetworkInterface.list(
        type: InternetAddressType.IPv4,
        includeLoopback: false,
      );
      // ignore: avoid_catches_without_on_clauses
    } catch (_) {
      return const []; // no interface enumeration (sandbox?) — mDNS path remains
    }
    final bases = <String>{};
    final own = <String>{};
    for (final i in interfaces) {
      if (_isTunnel(i)) continue;
      for (final a in i.addresses) {
        final parts = a.address.split('.');
        if (parts.length == 4) {
          bases.add(parts.sublist(0, 3).join('.'));
          own.add(a.address);
        }
      }
    }
    return [
      for (final base in bases)
        for (var n = 1; n < 255; n++)
          if (!own.contains('$base.$n')) '$base.$n',
    ];
  }

  /// [_probe] against one host and port, for the loopback test of the real
  /// HTTP path (#1129 review).
  @visibleForTesting
  static Future<ProbeResult> probeForTest(String host, int port) async {
    final client = HttpClient()..connectionTimeout = const Duration(milliseconds: 800);
    try {
      return await _probe(client, host, port: port);
    } finally {
      client.close(force: true);
    }
  }

  /// GET /api/v1/server/info with tight timeouts; a parseable payload with
  /// a server_uuid is the "this really is an Ara daemon" check. Any failure
  /// (refused, timeout, non-JSON) means "not a daemon" — never an error. The
  /// result also says whether the host answered at all and whether the OS
  /// refused the connection, for [sweepSaysBlocked].
  static Future<ProbeResult> _probe(HttpClient client, String host, {int port = defaultPort}) async {
    var answered = false;
    try {
      final req = await client
          .getUrl(Uri.parse('http://$host:$port/api/v1/server/info'))
          .timeout(const Duration(milliseconds: 900));
      final res = await req.close().timeout(const Duration(milliseconds: 1200));
      answered = true;
      if (res.statusCode != 200) return const ProbeResult(answered: true);
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
        return const ProbeResult(answered: true);
      }
      final nickname = json['nickname'];
      return ProbeResult(
        answered: true,
        server: AraServer(
          hostname: host,
          port: port,
          mdnsName: nickname is String && nickname.isNotEmpty ? nickname : null,
        ),
      );
      // ignore: avoid_catches_without_on_clauses
    } catch (e) {
      // Not an Ara daemon (or unreachable) — skip silently.
      final outcome = probeErrorOutcome(e);
      return ProbeResult(answered: answered || outcome.answered, blocked: outcome.blocked);
    }
  }
}

/// What one sweep probe learned about its host. Public for the sweep's test
/// seams ([ServerDiscoveryService.probeHost]).
class ProbeResult {
  const ProbeResult({this.server, this.answered = false, this.blocked = false});

  /// The Ara daemon found there, if any.
  final AraServer? server;

  /// The host responded at all (HTTP, or a TCP refusal of the port).
  final bool answered;

  /// The OS refused the connection (Local Network denial, sandbox).
  final bool blocked;
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
