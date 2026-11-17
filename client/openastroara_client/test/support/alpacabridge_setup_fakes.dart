import 'dart:async';

import 'package:dio/dio.dart';
import 'package:openastroara/models/alpacabridge_setup.dart';
import 'package:openastroara/models/discovered_device.dart';
import 'package:openastroara/services/alpaca_device_names_api.dart';
import 'package:openastroara/services/equipment_discovery_api.dart';
import 'package:openastroara/state/equipment/alpacabridge_setup_state.dart';
import 'package:openastroara/state/settings/equipment_connection_state.dart';

/// A made-up ZWO camera driver as AlpacaBridge's devicecatalog sends it (camelCase keys).
Map<String, Object?> zwoCameraDriverJson({
  List<Map<String, Object?>> extraFields = const [],
}) => {
  'vendor': 'zwo',
  'deviceType': 'camera',
  'displayName': 'ZWO ASI camera',
  'vendorLabel': 'ZWO',
  'available': true,
  'fields': [
    {
      'key': 'cameraIndex',
      'type': 'int',
      'default': -1,
      'required': false,
      'secret': false,
      'role': 'EnumerationIndex',
    },
    {
      'key': 'serialNumber',
      'type': 'string',
      'default': '',
      'required': false,
      'secret': false,
      'role': 'Plain',
    },
    {
      'key': 'cameraName',
      'type': 'string',
      'default': '',
      'required': false,
      'secret': false,
      'role': 'Plain',
    },
    ...extraFields,
  ],
};

BridgeDriver zwoCameraDriver({
  List<Map<String, Object?>> extraFields = const [],
}) => BridgeDriver.fromJson(zwoCameraDriverJson(extraFields: extraFields));

/// A required field with no default: a row for it is "Needs details".
const requiredNoDefault = {
  'key': 'usbPath',
  'type': 'string',
  'required': true,
  'secret': false,
  'role': 'Plain',
};

/// A made-up QHY camera driver: a second camera vendor, so a hard-coded `zwo` shows.
BridgeDriver qhyCameraDriver() => BridgeDriver.fromJson({
  ...zwoCameraDriverJson(),
  'vendor': 'qhy',
  'displayName': 'QHY camera',
  'vendorLabel': 'QHY',
});

/// A non-camera driver: `cameras(vendor)` must not be asked for its vendor.
BridgeDriver zwoFocuserDriver() => BridgeDriver.fromJson({
  'vendor': 'zwo',
  'deviceType': 'focuser',
  'displayName': 'ZWO EAF',
  'vendorLabel': 'ZWO',
  'available': true,
  'fields': <Object?>[],
});

/// Scriptable bridge: every call is recorded in [calls] in order.
class FakeBridgeClient implements AlpacaBridgeSetupClient {
  FakeBridgeClient({
    this.drivers = const [],
    this.cameraList = const [],
    this.otherVendors = const {},
    this.outcomes = const {},
    this.camerasThrow = false,
    this.gated = false,
  });

  List<BridgeDriver> drivers;

  /// The cameras of vendor `zwo`; [otherVendors] holds the rest, keyed by vendor.
  List<AttachedCamera> cameraList;
  Map<String, List<AttachedCamera>> otherVendors;

  /// Outcome per `serialNumber` of the body; missing = added.
  Map<String, AddOutcome> outcomes;
  bool camerasThrow;

  /// When true an add waits for [release] (one call per release), so a test can look
  /// at the state while an add is in flight.
  bool gated;
  final calls = <String>[];
  final bodies = <Map<String, Object?>>[];
  final vendorsAsked = <String>[];
  final addresses = <BridgeAddress>[];
  int cameraReads = 0;
  int inFlight = 0;
  int maxInFlight = 0;
  final _waiting = <Completer<void>>[];

  /// The factory the state asks for a client with; it records the address each client is built for.
  AlpacaBridgeSetupClient Function(BridgeAddress) get factory => (address) {
    addresses.add(address);
    return this;
  };

  /// Lets the oldest waiting add finish.
  void release() => _waiting.removeAt(0).complete();

  /// Adds waiting now (gated).
  int get waiting => _waiting.length;

  @override
  Future<List<BridgeDriver>> catalog() async => drivers;

  @override
  Future<List<AttachedCamera>> cameras(String vendor) async {
    cameraReads++;
    vendorsAsked.add(vendor);
    if (camerasThrow) {
      throw DioException(
        requestOptions: RequestOptions(path: '/cameras'),
        message: 'down',
      );
    }
    return vendor == 'zwo' ? cameraList : otherVendors[vendor] ?? const [];
  }

  @override
  Future<AddOutcome> addDevice(Map<String, Object?> body) async {
    final serial = '${body['serialNumber']}';
    calls.add(serial);
    bodies.add(Map.of(body));
    inFlight++;
    if (inFlight > maxInFlight) maxInFlight = inFlight;
    if (gated) {
      final gate = Completer<void>();
      _waiting.add(gate);
      await gate.future;
    }
    inFlight--;
    return outcomes[serial] ??
        const AddAdded('Device registered successfully.');
  }

  @override
  void close() {}
}

/// The daemon's discover route: [configured] is what the bridge already offers (the re-read,
/// and the host and port the bridge address falls back to).
class FakeConfiguredApi implements EquipmentDiscoveryApi {
  FakeConfiguredApi([this.configured = const []]);
  List<DiscoveredDevice> configured;
  int cameraScans = 0;

  /// `forceRefresh` of every camera scan, in order (a cached answer is stale after an add).
  final cameraForceRefresh = <bool>[];

  @override
  Future<List<DiscoveredDevice>> discover(
    EquipmentDeviceType type, {
    bool forceRefresh = false,
  }) async {
    if (type != EquipmentDeviceType.camera) return const [];
    cameraScans++;
    cameraForceRefresh.add(forceRefresh);
    return configured;
  }

  @override
  void close() {}
}

/// The daemon's device-names route: what the bridge has configured, with serials (the hide rule).
class FakeNames extends AlpacaDeviceNamesClient {
  FakeNames([this.devices = const {}]);

  /// Keyed `"camera/0"` as the daemon sends it.
  Map<String, AlpacaDeviceInfo> devices;
  final reads = <BridgeAddress>[];

  @override
  Future<Map<String, AlpacaDeviceInfo>> fetchDevices(
    String host,
    int port,
  ) async {
    reads.add(BridgeAddress(host, port));
    return devices;
  }

  @override
  void close() {}
}

AlpacaDeviceInfo configuredInfo(String name, {String serial = ''}) =>
    AlpacaDeviceInfo(name: name, serial: serial);

AttachedCamera camera(int index, String name, String serial) =>
    AttachedCamera(index: index, name: name, serial: serial);

DiscoveredDevice configuredCamera(String name) => DiscoveredDevice(
  uniqueId: 'id-$name',
  name: name,
  deviceType: EquipmentDeviceType.camera,
  hostName: '192.0.2.10',
  ipAddress: '192.0.2.10',
  ipPort: 32323,
  alpacaDeviceNumber: 0,
  useHttps: false,
);
