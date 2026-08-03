import 'dart:async';
import 'dart:convert';
import 'dart:io';
import 'dart:typed_data' show BytesBuilder;

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
  ServerDiscoveryService({this.mdnsSource, this.sweepSource});

  final Stream<AraServer> Function()? mdnsSource;
  final Stream<AraServer> Function()? sweepSource;

  /// Run a single discovery pass. mDNS starts immediately; the sweep joins
  /// only if mDNS stays empty (grace timer) or finishes empty. Results
  /// dedupe by endpoint; the stream closes when every started strategy is
  /// done.
  Stream<AraServer> discover() {
    final controller = StreamController<AraServer>();
    final seen = <String>{};
    var pending = 1; // the mDNS strand; the sweep adds itself if started
    var sawMdnsResult = false;
    var sweepStarted = false;
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
      if (sweepStarted || controller.isClosed) return;
      sweepStarted = true;
      pending++;
      (sweepSource ?? _sweepDiscover)()
          .listen(emit, onError: (Object _) {}, onDone: done);
    }

    (mdnsSource ?? _mdnsDiscover)().listen(
      (s) {
        sawMdnsResult = true;
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
    grace = Timer(mdnsGracePeriod, () {
      if (!sawMdnsResult) maybeStartSweep();
    });
    return controller.stream;
  }

  Stream<AraServer> _mdnsDiscover() async* {
    final mdns = MDnsClient();
    try {
      await mdns.start();
      await for (final PtrResourceRecord ptr in mdns.lookup<PtrResourceRecord>(
          ResourceRecordQuery.serverPointer(serviceType))) {
        await for (final SrvResourceRecord srv
            in mdns.lookup<SrvResourceRecord>(
                ResourceRecordQuery.service(ptr.domainName))) {
          // Resolve the SRV target to its numeric IPv4 while the multicast
          // channel is provably working (we just heard the record). Saving
          // the .local hostname instead locks the saved server to mDNS
          // resolution forever — on a flaky-multicast network the daemon
          // then reads as "down" even though it answers by IP.
          final String host;
          try {
            final a = await mdns
                .lookup<IPAddressResourceRecord>(
                    ResourceRecordQuery.addressIPv4(srv.target))
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

  /// Probe every host of every local /24 for an Ara daemon on [defaultPort],
  /// in bounded batches. Worst case (silent-drop hosts) a batch rides its
  /// slowest probe's timeouts, so the sweep can take several seconds on
  /// hostile networks — acceptable for a fallback that only runs when mDNS
  /// found nothing.
  Stream<AraServer> _sweepDiscover() async* {
    final List<NetworkInterface> interfaces;
    try {
      interfaces = await NetworkInterface.list(
          type: InternetAddressType.IPv4, includeLoopback: false);
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
      const tunnelPrefixes = ['utun', 'tun', 'tap', 'ppp', 'wg', 'zt', 'ipsec', 'gpd'];
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
      final res =
          await req.close().timeout(const Duration(milliseconds: 1200));
      if (res.statusCode != 200) return null;
      // Byte-capped read (review r3): probes hit arbitrary subnet hosts, and
      // a device that trickles a large body just under the time cap would
      // hold its slot ~3 s. /server/info is a few hundred bytes; anything
      // past 8 KiB is not an Ara daemon.
      const maxBodyBytes = 8192;
      final bytes = await res.fold<BytesBuilder>(BytesBuilder(copy: false),
          (b, chunk) {
        if (b.length + chunk.length > maxBodyBytes) {
          throw const FormatException('body too large for /server/info');
        }
        return b..add(chunk);
      }).timeout(const Duration(milliseconds: 1200));
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
