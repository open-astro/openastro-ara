import 'dart:async';

import '../models/discovered_device.dart';
import '../models/server.dart';
import '../services/camera_geometry_api.dart';
import '../services/equipment_device_api.dart';
import '../services/equipment_discovery_api.dart';
import '../services/filter_wheel_names_api.dart';
import '../services/focuser_props_api.dart';
import '../services/rotator_props_api.dart';
import '../services/telescope_optics_api.dart';
import '../state/settings/equipment_connection_state.dart';

/// §76.3 seam between the wizard's readiness notifier and the daemon: resolve
/// an assigned device id to its discovery record, connect it, and read its
/// facts. An interface so readiness tests run on a pure fake (no Dio, no
/// polling delays); [AraDeviceFactsSource] is the production implementation.
abstract interface class DeviceFactsSource {
  /// The discovery record for [assignedId], or null when it isn't on the
  /// bridge right now. Throws on transport failure.
  Future<DiscoveredDevice?> resolve(EquipmentDeviceType type, String assignedId);

  /// Connect [device] and wait until the daemon reports it (bounded); returns
  /// false when the connect never completes. Throws on transport failure.
  Future<bool> connect(DiscoveredDevice device);

  // Per-type fact reads — the daemon's status endpoints, null when the device
  // is connected but reports nothing usable (each API's own contract).
  Future<CameraGeometry?> cameraGeometry();
  Future<TelescopeOptics?> telescopeOptics();
  Future<MountProps?> mountProps();
  Future<FilterWheelSlots?> filterWheelSlots();
  Future<FocuserProps?> focuserProps();
  Future<RotatorProps?> rotatorProps();

  void close();
}

/// Production [DeviceFactsSource]: discovery + connect via the daemon's
/// equipment API (Alpaca connects take seconds on real hardware, so [connect]
/// polls the status endpoint like the §37 wizard screens did), facts via the
/// existing per-type read APIs.
class AraDeviceFactsSource implements DeviceFactsSource {
  final AraServer _server;
  final EquipmentDiscoveryApi _discovery;

  /// Injectable poll pacing so widget tests never sit through real waits.
  final Duration pollInterval;
  final int pollAttempts;

  AraDeviceFactsSource(this._server,
      {this.pollInterval = const Duration(milliseconds: 750),
      this.pollAttempts = 20})
      : _discovery = EquipmentDiscoveryApi(_server);

  @override
  Future<DiscoveredDevice?> resolve(
      EquipmentDeviceType type, String assignedId) async {
    final devices = await _discovery.discover(type);
    for (final d in devices) {
      if (d.uniqueId == assignedId) return d;
    }
    return null;
  }

  @override
  Future<bool> connect(DiscoveredDevice device) async {
    final api = EquipmentDeviceApi<Map<String, dynamic>>(
      _server,
      path: DiscoveredDevice.pathSegmentFor(device.deviceType),
      fromJson: (j) => j,
    );
    try {
      await api.connect(device);
      for (var i = 0; i < pollAttempts; i++) {
        await Future<void>.delayed(pollInterval);
        try {
          final status = await api.getStatus();
          if (status != null && status['state'] == 'connected') return true;
        } on Exception {
          // A transient blip mid-window (review r2) must not turn a device
          // that connects on the next poll into an "unreachable" card —
          // keep polling; only exhausting the window reports failure.
        }
      }
      return false;
    } finally {
      api.close();
    }
  }

  @override
  Future<CameraGeometry?> cameraGeometry() => CameraGeometryApi(_server).read();

  @override
  Future<TelescopeOptics?> telescopeOptics() =>
      TelescopeOpticsApi(_server).read();

  @override
  Future<MountProps?> mountProps() => TelescopeOpticsApi(_server).readProps();

  @override
  Future<FilterWheelSlots?> filterWheelSlots() =>
      FilterWheelNamesApi(_server).read();

  @override
  Future<FocuserProps?> focuserProps() => FocuserPropsApi(_server).read();

  @override
  Future<RotatorProps?> rotatorProps() => RotatorPropsApi(_server).read();

  @override
  void close() => _discovery.close();
}
