import 'package:flutter/foundation.dart';

// Client mirror of the daemon's multi-instance Switch surface
// (`GET /api/v1/equipment/switch` → `SwitchDto[]`). Each connected ASCOM Switch
// device is addressed by its `alpaca_device_number` (the `{n}` in
// `/api/v1/equipment/switch/{n}`); a device exposes one or more ports.

/// Connection state token, lowercased to match the daemon's `EquipmentConnectionState`
/// serialization. `unknown` is a client-only fallback for an unrecognized token.
enum SwitchConnectionState { disconnected, connecting, connected, error, unknown }

SwitchConnectionState _connectionFromWire(String? token) {
  switch (token) {
    case 'disconnected':
      return SwitchConnectionState.disconnected;
    case 'connecting':
      return SwitchConnectionState.connecting;
    case 'connected':
      return SwitchConnectionState.connected;
    case 'error':
      return SwitchConnectionState.error;
    default:
      return SwitchConnectionState.unknown;
  }
}

/// One port (sub-switch) of a Switch device. Boolean ports report `min == 0`,
/// `max == 1`; value ports (PWM/dimmable) carry a real range.
class SwitchPort {
  final int id;
  final String name;
  final double value;
  final double min;
  final double max;
  final bool canWrite;

  const SwitchPort({
    required this.id,
    required this.name,
    required this.value,
    required this.min,
    required this.max,
    required this.canWrite,
  });

  /// A two-state on/off port (range [0,1]) as opposed to a value/PWM port. ASCOM
  /// hardware can report the bounds with float noise (e.g. 0.9999999), so compare
  /// with a small epsilon rather than `==`. (A 0..1 PWM port is indistinguishable
  /// from boolean on min/max alone — the REST port DTO doesn't carry step size — so
  /// the UI treats any [0,1] range as a toggle.)
  bool get isBoolean => (min - 0).abs() < 1e-6 && (max - 1).abs() < 1e-6;

  factory SwitchPort.fromJson(Map<String, dynamic> json) {
    double dbl(String key) => (json[key] as num?)?.toDouble() ?? 0;
    return SwitchPort(
      id: (json['id'] as num?)?.toInt() ?? 0,
      name: json['name'] as String? ?? '',
      value: dbl('value'),
      min: dbl('min'),
      max: dbl('max'),
      canWrite: json['can_write'] as bool? ?? false,
    );
  }

  @override
  bool operator ==(Object other) =>
      identical(this, other) ||
      (other is SwitchPort &&
          other.id == id &&
          other.name == name &&
          other.value == value &&
          other.min == min &&
          other.max == max &&
          other.canWrite == canWrite);

  @override
  int get hashCode => Object.hash(id, name, value, min, max, canWrite);
}

/// A connected (or known-but-disconnected) Switch device and its ports.
class SwitchDevice {
  final String deviceId;
  final int alpacaDeviceNumber;
  final String name;
  final SwitchConnectionState connectionState;
  final List<SwitchPort> ports;

  const SwitchDevice({
    required this.deviceId,
    required this.alpacaDeviceNumber,
    required this.name,
    required this.connectionState,
    required this.ports,
  });

  bool get isConnected => connectionState == SwitchConnectionState.connected;

  factory SwitchDevice.fromJson(Map<String, dynamic> json) {
    final rawPorts = json['ports'];
    final ports = rawPorts is List
        ? rawPorts
            .whereType<Map<String, dynamic>>()
            .map(SwitchPort.fromJson)
            .toList(growable: false)
        : const <SwitchPort>[];
    // alpaca_device_number is the SOLE address key for the multi-switch feature —
    // a missing/garbled value would collide two devices on address 0. The daemon
    // always sends it; assert in debug (mirroring _parseType's default) so schema
    // drift surfaces immediately, while release still degrades to 0 rather than
    // crashing the list.
    final rawDeviceNumber = json['alpaca_device_number'];
    assert(rawDeviceNumber is num,
        'SwitchDto is missing alpaca_device_number — the multi-switch address key');
    return SwitchDevice(
      deviceId: json['device_id'] as String? ?? '',
      alpacaDeviceNumber: (rawDeviceNumber as num?)?.toInt() ?? 0,
      name: json['name'] as String? ?? '',
      connectionState: _connectionFromWire(json['state'] as String?),
      ports: ports,
    );
  }

  @override
  bool operator ==(Object other) =>
      identical(this, other) ||
      (other is SwitchDevice &&
          other.deviceId == deviceId &&
          other.alpacaDeviceNumber == alpacaDeviceNumber &&
          other.name == name &&
          other.connectionState == connectionState &&
          listEquals(other.ports, ports));

  @override
  int get hashCode => Object.hash(
        deviceId,
        alpacaDeviceNumber,
        name,
        connectionState,
        Object.hashAll(ports),
      );
}
