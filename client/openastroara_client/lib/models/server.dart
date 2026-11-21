/// Represents a discovered or manually-added OpenAstro Ara server.
///
/// Either populated from an mDNS scan result (`_openastroara._tcp.local`) or
/// from a manual user entry on the first-run screen. Once one is picked and
/// the handshake against `/api/v1/server/info` returns 200, it is kept for the
/// session only (#1129); only its name outlives the app, as the last rig.
class AraServer {
  final String hostname;
  final int port;
  final String? mdnsName;
  final String? serverVersion;

  /// The daemon's server_uuid, when the path that found it can say (the
  /// subnet sweep, iOS Bonjour's TXT record); the auto-connect matches on it.
  final String? uuid;

  const AraServer({
    required this.hostname,
    required this.port,
    this.mdnsName,
    this.serverVersion,
    this.uuid,
  });

  String get baseUrl => 'http://$hostname:$port';

  AraServer copyWith({String? serverVersion}) => AraServer(
        hostname: hostname,
        port: port,
        mdnsName: mdnsName,
        serverVersion: serverVersion ?? this.serverVersion,
        uuid: uuid,
      );

  @override
  bool operator ==(Object other) =>
      other is AraServer && other.hostname == hostname && other.port == port;

  @override
  int get hashCode => Object.hash(hostname, port);

  @override
  String toString() => 'AraServer($hostname:$port, mdns=$mdnsName, ver=$serverVersion, uuid=$uuid)';
}
