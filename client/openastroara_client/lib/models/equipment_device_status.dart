/// Connection-state token mirroring the daemon's `EquipmentConnectionState`
/// serialization (lowercased snake-case). `unknown` is a client-only fallback for
/// an unrecognized token so schema drift never crashes a panel.
enum EquipmentConnectionState { disconnected, connecting, connected, error, unknown }

EquipmentConnectionState equipmentConnectionStateFromWire(String? token) {
  switch (token) {
    case 'disconnected':
      return EquipmentConnectionState.disconnected;
    case 'connecting':
      return EquipmentConnectionState.connecting;
    case 'connected':
      return EquipmentConnectionState.connected;
    case 'error':
      return EquipmentConnectionState.error;
    default:
      return EquipmentConnectionState.unknown;
  }
}

/// Base for a single-instance equipment device's live status — the shared
/// `GET /api/v1/equipment/{type}` envelope (`device_id` + `name` + `state` +
/// device-specific runtime). The generic [EquipmentDeviceNotifier] engine drives
/// any model that exposes its [connectionState] through this base, so it can poll
/// a mid-connect device to settlement without knowing the device type.
abstract class EquipmentDeviceStatus {
  EquipmentConnectionState get connectionState;

  /// The device's display name (the `name` field of the status envelope); may be
  /// empty if the daemon didn't supply one.
  String get name;

  bool get isConnecting => connectionState == EquipmentConnectionState.connecting;
  bool get isConnected => connectionState == EquipmentConnectionState.connected;

  /// The device service keeps the last device after a disconnect and reports it
  /// with `state == disconnected` (a non-null status, NOT a 404), so callers must
  /// treat this as "no live device" — the same as a null status — rather than a
  /// connected one. See [EquipmentConnectionCard].
  bool get isDisconnected =>
      connectionState == EquipmentConnectionState.disconnected;

  /// Whether the device is mid-operation (e.g. a focuser moving, a dome slewing).
  /// Default `false`; device statuses with an activity sub-state override it. The
  /// generic engine fast-polls a connected-but-busy device to track it to rest.
  bool get isBusy => false;
}
