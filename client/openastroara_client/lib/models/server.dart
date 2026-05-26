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

  const AraServer({
    required this.hostname,
    required this.port,
    this.mdnsName,
    this.serverVersion,
  });

  String get baseUrl => 'http://$hostname:$port';

  AraServer copyWith({String? serverVersion}) => AraServer(
        hostname: hostname,
        port: port,
        mdnsName: mdnsName,
        serverVersion: serverVersion ?? this.serverVersion,
      );

  @override
  bool operator ==(Object other) =>
      other is AraServer && other.hostname == hostname && other.port == port;

  @override
  int get hashCode => Object.hash(hostname, port);

  @override
  String toString() => 'AraServer($hostname:$port, mdns=$mdnsName, ver=$serverVersion)';
}
