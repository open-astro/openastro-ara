/// A single §60.9 server event as delivered over the WebSocket stream
/// (`GET /api/v1/ws`). The wire envelope is `{type, ts, seq, payload}`
/// (snake_case); see `WsEventEnvelopeDto` on the server.
///
/// [seq] is a per-connection monotonic sequence number — the client tracks the
/// highest seen so it can resume after a reconnect (see [WsEventStream]).
class WsEvent {
  /// Event-type token, e.g. `guider.dark_library.complete` or
  /// `diagnostics.health_changed`. Routing is by this string.
  final String type;

  /// Server-assigned timestamp (RFC 3339 / ISO 8601, UTC).
  final DateTime ts;

  /// Monotonic per-connection sequence number.
  final int seq;

  /// Event-specific data. Always a JSON object on the wire; an empty map when
  /// the event carries no payload.
  final Map<String, dynamic> payload;

  const WsEvent({
    required this.type,
    required this.ts,
    required this.seq,
    this.payload = const <String, dynamic>{},
  });

  /// Parse one decoded envelope frame. Throws [FormatException] when the frame
  /// is missing the required `type`/`seq` fields or they are the wrong type, so
  /// a malformed frame fails loudly at the boundary rather than silently.
  factory WsEvent.fromJson(Map<String, dynamic> json) {
    final type = json['type'];
    final seq = json['seq'];
    if (type is! String || seq is! int) {
      throw FormatException('WsEvent frame missing string "type" / int "seq"', json);
    }
    final ts = json['ts'];
    return WsEvent(
      type: type,
      seq: seq,
      ts: ts is String ? DateTime.parse(ts) : DateTime.fromMillisecondsSinceEpoch(0, isUtc: true),
      payload: json['payload'] is Map<String, dynamic>
          ? json['payload'] as Map<String, dynamic>
          : const <String, dynamic>{},
    );
  }

  @override
  String toString() => 'WsEvent($type, seq=$seq, ts=$ts)';
}
