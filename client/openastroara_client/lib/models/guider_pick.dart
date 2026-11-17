/// What a Guide Camera pick writes (ADR 0013 rule 4, #1861): the camera's
/// name, its Alpaca UniqueID ("" for a non-Alpaca choice or an unknown id) and
/// the Alpaca server's host and port (null when the choice has no endpoint).
/// `guiderCameraId` is not part of a pick.
class GuiderPick {
  const GuiderPick({
    required this.camera,
    this.uniqueId = '',
    this.host,
    this.port,
  });

  final String camera;
  final String uniqueId;
  final String? host;
  final int? port;
}

/// Parsed Alpaca endpoint from a daemon camera-choice string, e.g.
/// `"Alpaca Camera [rc91.lan:6800/1]"` → host `rc91.lan`, port `6800`,
/// device `1`. Null when the choice doesn't carry the `[host:port/N]` suffix
/// (non-Alpaca drivers, `"None"`). Pure — unit-tested.
({String host, int port, int device})? parseAlpacaChoiceEndpoint(
  String choice,
) {
  final m = RegExp(r'\[([^\[\]:]+):(\d+)/(\d+)\]\s*$').firstMatch(choice);
  if (m == null) return null;
  final port = int.tryParse(m.group(2)!);
  final device = int.tryParse(m.group(3)!);
  if (port == null || port < 1 || port > 65535 || device == null) return null;
  return (host: m.group(1)!, port: port, device: device);
}
