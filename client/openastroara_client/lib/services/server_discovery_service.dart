import 'package:multicast_dns/multicast_dns.dart';

import '../models/server.dart';

/// Scans the local network for `_openastroara._tcp.local` servers via mDNS.
/// Per playbook §30 first-run flow + §60.1 mDNS service-type registration.
class ServerDiscoveryService {
  static const String serviceType = '_openastroara._tcp.local';

  /// Run a single discovery pass. The returned iterable is consumed lazily —
  /// the mDNS client stays open until the caller stops iterating. Use a
  /// `timeout` of a few seconds in the caller (the daemon side broadcasts
  /// every couple of seconds, so 3-5s catches typical responses).
  Stream<AraServer> discover() async* {
    final mdns = MDnsClient();
    await mdns.start();
    try {
      await for (final PtrResourceRecord ptr in mdns.lookup<PtrResourceRecord>(
          ResourceRecordQuery.serverPointer(serviceType))) {
        await for (final SrvResourceRecord srv
            in mdns.lookup<SrvResourceRecord>(
                ResourceRecordQuery.service(ptr.domainName))) {
          yield AraServer(
            hostname: srv.target,
            port: srv.port,
            mdnsName: ptr.domainName,
          );
        }
      }
    } finally {
      mdns.stop();
    }
  }
}
