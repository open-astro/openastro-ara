import 'package:dio/dio.dart';
import 'package:openastroara/models/alpacabridge_setup.dart';
import 'package:openastroara/models/discovered_device.dart';
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

/// Scriptable bridge: every call is recorded in [calls] in order.
class FakeBridgeClient implements AlpacaBridgeSetupClient {
  FakeBridgeClient({
    this.drivers = const [],
    this.cameraList = const [],
    this.outcomes = const {},
    this.camerasThrow = false,
  });

  List<BridgeDriver> drivers;
  List<AttachedCamera> cameraList;

  /// Outcome per `serialNumber` of the body; missing = added.
  Map<String, AddOutcome> outcomes;
  bool camerasThrow;
  final calls = <String>[];
  final bodies = <Map<String, Object?>>[];
  int cameraReads = 0;

  @override
  Future<List<BridgeDriver>> catalog() async => drivers;

  @override
  Future<List<AttachedCamera>> cameras(String vendor) async {
    cameraReads++;
    if (camerasThrow)
      throw DioException(
        requestOptions: RequestOptions(path: '/cameras'),
        message: 'down',
      );
    return vendor == 'zwo' ? cameraList : const [];
  }

  @override
  Future<AddOutcome> addDevice(Map<String, Object?> body) async {
    final serial = '${body['serialNumber']}';
    calls.add(serial);
    bodies.add(Map.of(body));
    return outcomes[serial] ??
        const AddAdded('Device registered successfully.');
  }

  @override
  void close() {}
}

/// The daemon's discover route: [configured] is what the bridge already offers (the re-read).
class FakeConfiguredApi implements EquipmentDiscoveryApi {
  FakeConfiguredApi([this.configured = const []]);
  List<DiscoveredDevice> configured;
  int cameraScans = 0;

  @override
  Future<List<DiscoveredDevice>> discover(
    EquipmentDeviceType type, {
    bool forceRefresh = false,
  }) async {
    if (type != EquipmentDeviceType.camera) return const [];
    cameraScans++;
    return configured;
  }

  @override
  void close() {}
}

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
