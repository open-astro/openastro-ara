/// Represents a discovered or manually-added OpenAstro Ara server.
///
/// Either populated from an mDNS scan result (`_openastroara._tcp.local`) or
/// from a manual user entry on the first-run screen. Once the user picks one
/// and the handshake against `/api/v1/server/info` returns 200, this is what
/// gets persisted to local secure storage (per playbook §30).
class AraServer {
  final String hostname;
  final int port;
  final String? mdnsName;
  final String? serverVersion;

  /// The daemon's `server_uuid` from `/api/v1/server/info`: the rig's identity,
  /// independent of the address it happens to have today. Lets the client find
  /// the same rig again after DHCP moves it (#1129); null for entries saved
  /// before it was recorded and for unconfirmed mDNS results.
  final String? serverUuid;

  const AraServer({
    required this.hostname,
    required this.port,
    this.mdnsName,
    this.serverVersion,
    this.serverUuid,
  });

  String get baseUrl => 'http://$hostname:$port';

  AraServer copyWith({String? serverVersion, String? serverUuid, String? mdnsName}) => AraServer(
        hostname: hostname,
        port: port,
        mdnsName: mdnsName ?? this.mdnsName,
        serverVersion: serverVersion ?? this.serverVersion,
        serverUuid: serverUuid ?? this.serverUuid,
      );

  @override
  bool operator ==(Object other) =>
      other is AraServer && other.hostname == hostname && other.port == port;

  @override
  int get hashCode => Object.hash(hostname, port);

  @override
  String toString() => 'AraServer($hostname:$port, mdns=$mdnsName, ver=$serverVersion, uuid=$serverUuid)';
}
