/// Parse a user-entered `host[:port]`, handling IPv6 literals:
///  - "[::1]"          → host only, no port (ends with ']')
///  - "[::1]:4400"     → split on the LAST colon (after the closing bracket)
///  - "::1" / "fe80::1" → bare (unbracketed) IPv6, host only — a value with 2+
///                        colons and no brackets is an IPv6 address, since a
///                        port on IPv6 requires [brackets] (RFC 3986 §3.2.2)
///  - "localhost"      → host only
///  - "localhost:4400" → split on the colon
///
/// Empty parts come back null so callers can layer their own fallbacks
/// (profile base value, protocol default). Shared by the wizard's PHD2 save
/// mapper and its "Test connection" button so the two can't drift.
({String? host, int? port}) parseHostPort(String raw) {
  final hp = raw.trim();
  final String hostPart;
  final String portPart;
  if (hp.endsWith(']') ||
      (!hp.contains(']') && ':'.allMatches(hp).length >= 2)) {
    // Bracketed IPv6 with no port, OR a bare IPv6 literal: no port to split off.
    hostPart = hp;
    portPart = '';
  } else {
    // Reached only by `[ipv6]:port`, `host:port`, `host`, or `:port` — so for a
    // bracketed address here (`[::1]:4401`) lastIndexOf(':') lands on the colon
    // *after* the `]`, i.e. the real port separator.
    final idx = hp.lastIndexOf(':');
    hostPart = idx >= 0 ? hp.substring(0, idx).trim() : hp;
    portPart = idx >= 0 ? hp.substring(idx + 1).trim() : '';
  }
  return (
    host: hostPart.isNotEmpty ? hostPart : null,
    port: portPart.isNotEmpty ? int.tryParse(portPart) : null,
  );
}
