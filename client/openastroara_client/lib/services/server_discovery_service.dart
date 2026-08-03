import 'dart:async';
import 'dart:convert';
import 'dart:io';

import 'package:multicast_dns/multicast_dns.dart';

import '../models/server.dart';

/// Scans the local network for Ara daemons two ways at once:
///
/// 1. mDNS (`_openastroara._tcp.local`) per playbook §30 first-run flow +
///    §60.1 service-type registration — instant when multicast works.
/// 2. A direct subnet sweep probing `GET /api/v1/server/info` on port 5555
///    across every local /24. The raw-socket mDNS package is unreliable on
///    macOS (the OS's own mDNSResponder owns port 5353 — `dns-sd` sees the
///    daemon while the in-app browse stays empty; site outage 2026-08-03),
///    so discovery must not DEPEND on multicast. The info endpoint verifies
///    the responder really is an Ara daemon before it's listed.
///
/// Both paths yield numeric-IP hostnames, so the saved server never carries
/// a `.local` name that only resolves while multicast is healthy.
class ServerDiscoveryService {
  static const String serviceType = '_openastroara._tcp.local';
  static const int defaultPort = 5555;

  /// Run a single discovery pass: both strategies race, results dedupe by
  /// endpoint. The stream closes when both finish (the sweep bounds itself
  /// with per-probe timeouts; the mDNS lookups end on their own timeout).
  Stream<AraServer> discover() {
    final controller = StreamController<AraServer>();
    final seen = <String>{};
    var pending = 2;
    void done() {
      if (--pending == 0 && !controller.isClosed) {
        unawaited(controller.close());
      }
    }

    void emit(AraServer s) {
      if (!controller.isClosed && seen.add('${s.hostname}:${s.port}')) {
        controller.add(s);
      }
    }

    _mdnsDiscover().listen(emit, onError: (Object _) {}, onDone: done);
    _sweepDiscover().listen(emit, onError: (Object _) {}, onDone: done);
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
          // then reads as "down" even though it answers by IP. The name is
          // kept for display via [AraServer.mdnsName].
          var host = srv.target;
          await for (final IPAddressResourceRecord a
              in mdns.lookup<IPAddressResourceRecord>(
                  ResourceRecordQuery.addressIPv4(srv.target))) {
            host = a.address.address;
            break;
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

  /// Probe every host of every local /24 for an Ara daemon on [defaultPort].
  /// ~254 parallel probes per interface with sub-second timeouts: completes
  /// in ~1-2 s on a LAN and finds the daemon with zero multicast dependency.
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
      final probes = <Future<AraServer?>>[
        for (final base in bases)
          for (var n = 1; n < 255; n++)
            if (!own.contains('$base.$n')) _probe(client, '$base.$n'),
      ];
      for (final p in probes) {
        final s = await p; // all started in parallel; awaiting collects them
        if (s != null) yield s;
      }
    } finally {
      client.close(force: true);
    }
  }

  /// GET /api/v1/server/info with a tight timeout; a parseable payload with
  /// a server_uuid is the "this really is an Ara daemon" check. Any failure
  /// (refused, timeout, non-JSON) means "not a daemon" — never an error.
  Future<AraServer?> _probe(HttpClient client, String host) async {
    try {
      final req = await client
          .getUrl(Uri.parse('http://$host:$defaultPort/api/v1/server/info'))
          .timeout(const Duration(milliseconds: 900));
      final res = await req.close().timeout(const Duration(seconds: 2));
      if (res.statusCode != 200) return null;
      final body = await res
          .transform(utf8.decoder)
          .join()
          .timeout(const Duration(seconds: 2));
      final json = jsonDecode(body);
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
