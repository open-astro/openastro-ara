import 'package:dio/dio.dart';

import '../../models/profile_draft.dart';
import '../../models/server.dart';
import '../../services/equipment_discovery_api.dart';
import '../../state/settings/equipment_connection_state.dart';

/// §37 wizard finish — hand the assigned equipment to the daemon.
///
/// The wizard's device assignment is client-side draft state only; the daemon
/// learns which devices exist when it CONNECTS them (each successful connect is
/// remembered server-side for auto-connect-on-boot, §52.1). Screens 4+ connect
/// CAM/FW/FOC/MOUNT/ROT/GUIDE as a side-effect of their "Refresh from device"
/// buttons, but the slot-only types (switch, weather, flat, safety, dome) have
/// no such screen — without this step they end the wizard assigned-but-never
/// -connected. Conversely a slot the user set to "None" must FORGET any stale
/// remembered device, or auto-connect keeps attempting hardware the user no
/// longer has (an error dot on the toolbar forever).
///
/// Best-effort per device: returns short human-readable notes for connects that
/// couldn't be dispatched (device gone from the bridge, network error); an
/// empty list means everything was handed off. Never throws.
Future<List<String>> applyWizardEquipment(
  AraServer server,
  EquipmentSlots slots,
) async {
  // No-op when nothing was assigned at all: the user skipped the discovery
  // step, so treating every slot as a deliberate "None" would disconnect and
  // forget a rig they configured elsewhere.
  final singles = <(EquipmentDeviceType, String?, String)>[
    (EquipmentDeviceType.camera, slots.cameraDeviceId, 'Camera'),
    (EquipmentDeviceType.filterWheel, slots.filterWheelDeviceId, 'Filter wheel'),
    (EquipmentDeviceType.focuser, slots.focuserDeviceId, 'Focuser'),
    (EquipmentDeviceType.mount, slots.mountDeviceId, 'Mount'),
    (EquipmentDeviceType.rotator, slots.rotatorDeviceId, 'Rotator'),
    (EquipmentDeviceType.dome, slots.domeDeviceId, 'Dome'),
    (
      EquipmentDeviceType.weather,
      slots.observingConditionsDeviceId,
      'Weather'
    ),
    (
      EquipmentDeviceType.safetyMonitor,
      slots.safetyMonitorDeviceId,
      'Safety monitor'
    ),
    (EquipmentDeviceType.flatPanel, slots.flatPanelDeviceId, 'Flat panel'),
  ];
  final anyAssigned = slots.switchDeviceIds.isNotEmpty ||
      singles.any((s) => s.$2 != null);
  if (!anyAssigned) return const [];

  final notes = <String>[];
  final dio = Dio(BaseOptions(
    baseUrl: server.baseUrl,
    connectTimeout: const Duration(seconds: 3),
    receiveTimeout: const Duration(seconds: 5),
    sendTimeout: const Duration(seconds: 5),
  ));
  final discovery = EquipmentDiscoveryApi(server);
  try {
    for (final (type, assignedId, label) in singles) {
      final segment = _restSegment(type);
      if (assignedId == null) {
        // Deliberate "None" — clear the remembered selection AND drop any live
        // (possibly errored) connection so the toolbar dot goes grey now, not
        // after the next daemon restart. Both are idempotent; failures here are
        // cleanup-only and not worth alarming the user over.
        await _quietly(() => dio.delete<void>(
            '/api/v1/equipment/$segment/remembered'));
        await _quietly(
            () => dio.post<void>('/api/v1/equipment/$segment/disconnect'));
        continue;
      }
      final note = await _connect(dio, discovery, type, segment, assignedId, label);
      if (note != null) notes.add(note);
    }

    // Switch is multi-instance: connect each assigned hub; an empty list after
    // the user visited discovery means "no switches" — forget the stale ones.
    if (slots.switchDeviceIds.isEmpty) {
      await _quietly(
          () => dio.delete<void>('/api/v1/equipment/switch/remembered'));
    } else {
      for (final id in slots.switchDeviceIds) {
        final note = await _connect(
            dio, discovery, EquipmentDeviceType.switchDevice, 'switch', id,
            'Switch');
        if (note != null) notes.add(note);
      }
    }
  } finally {
    discovery.close();
    dio.close(force: true);
  }
  return notes;
}

/// Discover the type and dispatch a connect for [assignedId]. Returns a short
/// failure note, or null when the connect was accepted (202 — the daemon
/// finishes in the background).
Future<String?> _connect(
  Dio dio,
  EquipmentDiscoveryApi discovery,
  EquipmentDeviceType type,
  String segment,
  String assignedId,
  String label,
) async {
  try {
    final devices = await discovery.discover(type);
    final device =
        devices.where((d) => d.uniqueId == assignedId).firstOrNull;
    if (device == null) {
      return '$label: assigned device is no longer on the Alpaca bridge.';
    }
    await dio.post<void>(
      '/api/v1/equipment/$segment/connect',
      data: <String, dynamic>{'device': device.toConnectRequestJson()},
    );
    return null;
  } on DioException catch (e) {
    return '$label: ${e.message ?? 'network error'}';
  }
}

/// Run a best-effort cleanup call, swallowing network/HTTP errors.
Future<void> _quietly(Future<void> Function() call) async {
  try {
    await call();
  } on DioException {
    // cleanup only — nothing user-actionable
  }
}

/// The daemon's REST group segment per device type. Differs from the DISCOVERY
/// segment for the flat panel ('flatdevice' group vs 'covercalibrator'
/// discovery type) — see DiscoveredDevice.pathSegmentFor.
String _restSegment(EquipmentDeviceType t) => switch (t) {
      EquipmentDeviceType.camera => 'camera',
      EquipmentDeviceType.mount => 'telescope',
      EquipmentDeviceType.focuser => 'focuser',
      EquipmentDeviceType.filterWheel => 'filterwheel',
      EquipmentDeviceType.rotator => 'rotator',
      EquipmentDeviceType.guider => 'guider',
      EquipmentDeviceType.flatPanel => 'flatdevice',
      EquipmentDeviceType.dome => 'dome',
      EquipmentDeviceType.weather => 'observingconditions',
      EquipmentDeviceType.safetyMonitor => 'safetymonitor',
      EquipmentDeviceType.switchDevice => 'switch',
    };
