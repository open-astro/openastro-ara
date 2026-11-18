import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../models/alpacabridge_form.dart';
import '../../models/alpacabridge_setup.dart';
import '../../models/discovered_device.dart';
import '../../services/alpaca_device_names_api.dart';
import '../../services/alpacabridge_setup_api.dart';
import '../guider/guider_choice_state.dart';
import '../saved_server_state.dart';
import '../settings/equipment_connection_state.dart';
import '../wizard_state.dart';
import 'equipment_discovery_provider.dart';

export '../../services/alpacabridge_setup_api.dart'
    show AlpacaBridgeSetupClient;

// #1580 — add-your-devices: ONE provider for the Found list, shared by the setup wizard and
// Simple Mode (ADR 0009). A later AlpacaBridge detection endpoint changes only [FoundDevicesNotifier].

/// The types the Connect probe counts, camera first (the handshake); the re-read after an add
/// scans the same ones. Guider is not Alpaca-discoverable.
const equipmentProbeTypes = [
  EquipmentDeviceType.camera,
  EquipmentDeviceType.mount,
  EquipmentDeviceType.focuser,
  EquipmentDeviceType.filterWheel,
  EquipmentDeviceType.rotator,
  EquipmentDeviceType.flatPanel,
  EquipmentDeviceType.dome,
  EquipmentDeviceType.weather,
  EquipmentDeviceType.safetyMonitor,
  EquipmentDeviceType.switchDevice,
];

/// Builds the client for the bridge the daemon should dial. Overridable in tests.
final alpacaBridgeSetupClientFactoryProvider =
    Provider<AlpacaBridgeSetupClient Function(BridgeAddress)>((ref) {
      final server = ref.watch(activeServerProvider);
      return (a) => server == null
          ? const _NoServerClient()
          : AlpacaBridgeSetupApi(server, host: a.host, port: a.port);
    });

/// The wizard draft's bridge address, null elsewhere (Simple Mode has no draft). Read once;
/// [FoundDevicesNotifier.redetect] invalidates it to pick up an edit.
final bridgeAddressOverrideProvider = Provider<String?>(
  (ref) => ref.read(wizardControllerProvider).draft.alpacaBridgeAddress,
);

class _NoServerClient implements AlpacaBridgeSetupClient {
  const _NoServerClient();
  @override
  Future<List<BridgeDriver>> catalog() async => const [];
  @override
  Future<List<AttachedCamera>> cameras(String vendor) async => const [];
  @override
  Future<AddOutcome> addDevice(Map<String, Object?> body) async =>
      const AddUnconfirmed();
  @override
  void close() {}
}

enum FoundStatus { found, needsDetails, waiting, adding, added, failed }

/// One detected device. A secret never lives here (the notifier holds it apart).
class FoundRow {
  const FoundRow({
    required this.id,
    required this.vendor,
    required this.deviceType,
    required this.serial,
    required this.name,
    required this.ticked,
    required this.status,
    this.values = const {},
    this.error,
    this.unconfirmed = false,
    this.warning,
  });

  final String id;
  final String vendor;
  final String deviceType;
  final String serial;
  final String name;
  final bool ticked;
  final FoundStatus status;

  /// Values set through Details (never a secret, never a detected value).
  final Map<String, Object?> values;
  final String? error;
  final bool unconfirmed;
  final String? warning;

  String get driverKey => '$vendor-$deviceType';

  FoundRow copyWith({
    bool? ticked,
    FoundStatus? status,
    Map<String, Object?>? values,
    String? error,
    bool clearError = false,
    bool? unconfirmed,
    String? warning,
  }) => FoundRow(
    id: id,
    vendor: vendor,
    deviceType: deviceType,
    serial: serial,
    name: name,
    ticked: ticked ?? this.ticked,
    status: status ?? this.status,
    values: values ?? this.values,
    error: clearError ? null : error ?? this.error,
    unconfirmed: unconfirmed ?? this.unconfirmed,
    warning: warning ?? this.warning,
  );

  @override
  String toString() =>
      'FoundRow($id, $status, ticked: $ticked, values: $values)';
}

/// Rows ticked and still sendable (found or needs details): what Add selected counts.
int selectedCount(List<FoundRow> rows) => rows
    .where(
      (r) =>
          r.ticked &&
          (r.status == FoundStatus.found ||
              r.status == FoundStatus.needsDetails),
    )
    .length;

/// A sequence is under way: any row waiting or adding.
bool isRunning(List<FoundRow> rows) => rows.any(
  (r) => r.status == FoundStatus.waiting || r.status == FoundStatus.adding,
);

/// An add, a retry or a detection is in flight: one at a time, never parallel (an add can open a
/// vendor SDK). The panel greys its buttons while it is set.
final foundDevicesBusyProvider = NotifierProvider<FoundDevicesBusy, bool>(
  FoundDevicesBusy.new,
);

class FoundDevicesBusy extends Notifier<bool> {
  @override
  bool build() => false;

  void set(bool v) {
    if (ref.mounted) state = v;
  }
}

final foundDevicesProvider =
    AsyncNotifierProvider<FoundDevicesNotifier, List<FoundRow>>(
      FoundDevicesNotifier.new,
      // An error is shown with Retry detection; no silent backoff re-dials the bridge.
      retry: (_, _) => null,
    );

class FoundDevicesNotifier extends AsyncNotifier<List<FoundRow>> {
  var _drivers = <BridgeDriver>[];
  var _address = const BridgeAddress('localhost', 6800);
  // Secrets by row id, apart from the rows so no dump of the state prints one.
  final _secrets = <String, Map<String, String>>{};

  @override
  Future<List<FoundRow>> build() => _detect();

  List<FoundRow> get _rows => state.value ?? const [];

  void _set(List<FoundRow> rows) {
    if (ref.mounted) state = AsyncData(rows);
  }

  void _patch(String id, FoundRow Function(FoundRow) f) =>
      _set([for (final r in _rows) r.id == id ? f(r) : r]);

  bool get _busy => ref.read(foundDevicesBusyProvider);

  /// Runs [work] as the one operation in flight (null while another runs). The flag is set
  /// synchronously, before the first await, so a second call sees it.
  Future<T?> _exclusive<T>(Future<T> Function() work) async {
    if (_busy) return null;
    final busy = ref.read(foundDevicesBusyProvider.notifier)..set(true);
    try {
      return await work();
    } finally {
      busy.set(false);
    }
  }

  Future<List<FoundRow>> _detect() async {
    final server = ref.read(activeServerProvider);
    if (server == null) return const [];
    _secrets.clear();
    final override = ref.read(bridgeAddressOverrideProvider)?.trim() ?? '';
    // The address is settled before any call: a bad port is an error with nothing dialled.
    _address = resolveBridgeAddress(
      override: override,
      discovered: override.isEmpty ? await _firstDiscovered(server) : const [],
    );
    final client = ref.read(alpacaBridgeSetupClientFactoryProvider)(_address);
    final List<(BridgeDriver, AttachedCamera)> found = [];
    try {
      _drivers = await client.catalog();
      final cameraDrivers = _drivers.where((d) => d.deviceType == 'camera');
      for (final vendor in {for (final d in cameraDrivers) d.vendor}) {
        final driver = cameraDrivers.firstWhere((d) => d.vendor == vendor);
        for (final c in await client.cameras(vendor)) {
          found.add((driver, c));
        }
      }
    } finally {
      client.close();
    }
    if (_drivers.isEmpty) return const [];
    final configured = await ref
        .read(alpacaDeviceNamesApiProvider)
        .fetchDevices(_address.host, _address.port);
    return [
      for (final (driver, cam) in found)
        if (!_isConfigured(cam.name, cam.serial, configured))
          _newRow(driver, cam),
    ];
  }

  Future<List<DiscoveredDevice>> _firstDiscovered(dynamic server) async {
    final api = ref.read(equipmentDiscoveryApiFactoryProvider)(server);
    try {
      return await api.discover(EquipmentDeviceType.camera);
    } catch (_) {
      return const []; // falls back to localhost:6800
    } finally {
      api.close();
    }
  }

  // The serial decides; the name only when either side has none.
  static bool _isConfigured(
    String name,
    String serial,
    Map<String, AlpacaDeviceInfo> configured,
  ) => configured.entries.any((e) {
    if (!e.key.startsWith('camera/')) return false;
    final other = e.value.serial;
    return serial.isNotEmpty && other.isNotEmpty
        ? serial == other
        : e.value.name == name;
  });

  FoundRow _newRow(BridgeDriver driver, AttachedCamera cam) {
    final id =
        '${driver.vendor}:${driver.deviceType}:${cam.serial.isEmpty ? '#${cam.index}' : cam.serial}';
    final row = FoundRow(
      id: id,
      vendor: driver.vendor,
      deviceType: driver.deviceType,
      serial: cam.serial,
      name: cam.name,
      ticked: true,
      status: FoundStatus.found,
    );
    return row.copyWith(status: _readiness(row));
  }

  static const _detected = {'serialNumber', 'cameraName'};

  /// found, or needsDetails while a visible required field has no value and no default.
  FoundStatus _readiness(FoundRow row) {
    final driver = _drivers.firstWhere((d) => d.key == row.driverKey);
    final form = BridgeForm(driver);
    final values = {...form.initialValues(), ...row.values};
    for (final f in form.visibleFields(values)) {
      if (!f.required || _detected.contains(f.key)) continue;
      final Object? v;
      if (f.secret) {
        v = _secrets[row.id]?[f.key];
      } else {
        v = values[f.key];
      }
      if (v == null || '$v'.trim().isEmpty) return FoundStatus.needsDetails;
    }
    return FoundStatus.found;
  }

  BridgeDriver driverOf(FoundRow row) =>
      _drivers.firstWhere((d) => d.key == row.driverKey);

  void toggle(String id) {
    if (_busy) return;
    _patch(
      id,
      (r) =>
          r.status == FoundStatus.found || r.status == FoundStatus.needsDetails
          ? r.copyWith(ticked: !r.ticked)
          : r,
    );
  }

  /// Details for one row (the form's values; [secrets] kept in memory only).
  void setDetails(
    String id,
    Map<String, Object?> values, {
    Map<String, String> secrets = const {},
  }) {
    if (_busy) return;
    final row = _rows.where((r) => r.id == id).firstOrNull;
    if (row == null || row.status == FoundStatus.added) return;
    final form = BridgeForm(driverOf(row));
    final defaults = form.initialValues();
    // Only what differs from the default is sent: the bridge applies the rest.
    final kept = {
      for (final e in values.entries)
        if (!_detected.contains(e.key) && e.value != defaults[e.key])
          e.key: e.value,
    };
    if (secrets.isNotEmpty) _secrets[id] = {..._secrets[id] ?? {}, ...secrets};
    final updated = row.copyWith(
      values: kept,
      clearError: true,
      unconfirmed: false,
      ticked: row.status == FoundStatus.failed ? true : null,
    );
    _patch(id, (_) => updated.copyWith(status: _readiness(updated)));
  }

  Map<String, Object?> _body(FoundRow r) => {
    'vendor': r.vendor,
    'deviceType': r.deviceType,
    'serialNumber': r.serial,
    'cameraName': r.name,
    ...r.values,
    ...?_secrets[r.id],
  };

  Future<void> addSelected() async {
    final rows = _rows;
    if (rows.any((r) => r.ticked && r.status == FoundStatus.needsDetails)) {
      return;
    }
    final ids = [
      for (final r in rows)
        if (r.ticked && r.status == FoundStatus.found) r.id,
    ];
    if (ids.isEmpty) return;
    await _exclusive(() {
      _set([
        for (final r in rows)
          ids.contains(r.id) ? r.copyWith(status: FoundStatus.waiting) : r,
      ]);
      return _sequence(ids);
    });
  }

  /// Sends the rows one at a time in order, then re-reads once.
  Future<void> _sequence(List<String> ids, {bool checkLanded = false}) async {
    final client = ref.read(alpacaBridgeSetupClientFactoryProvider)(_address);
    try {
      for (final id in ids) {
        if (!ref.mounted) return;
        final row = _rows.firstWhere((r) => r.id == id);
        _patch(id, (r) => r.copyWith(status: FoundStatus.adding));
        if (checkLanded && row.unconfirmed && await _landed(row)) {
          _patch(
            id,
            (r) => r.copyWith(
              status: FoundStatus.added,
              ticked: true,
              clearError: true,
              unconfirmed: false,
            ),
          );
          continue;
        }
        AddOutcome out;
        try {
          out = await client.addDevice(_body(row));
        } catch (_) {
          out = const AddUnconfirmed();
        }
        _patch(
          id,
          (r) => switch (out) {
            AddAdded(:final warning) => r.copyWith(
              status: FoundStatus.added,
              ticked: true,
              clearError: true,
              unconfirmed: false,
              warning: warning,
            ),
            AddRefused(:final detail) => r.copyWith(
              status: FoundStatus.failed,
              ticked: false,
              error: detail,
              unconfirmed: false,
            ),
            AddUnconfirmed() => r.copyWith(
              status: FoundStatus.failed,
              ticked: false,
              unconfirmed: true,
            ),
          },
        );
        if (out is AddAdded) _secrets.remove(id);
      }
    } finally {
      client.close();
    }
    await _reread();
  }

  // An unconfirmed add may have landed: look before sending it again (no duplicate).
  Future<bool> _landed(FoundRow row) async => _isConfigured(
    row.name,
    row.serial,
    await ref
        .read(alpacaDeviceNamesApiProvider)
        .fetchDevices(_address.host, _address.port),
  );

  /// Refreshes what the rest of the app reads; never changes a row.
  Future<void> _reread() async {
    final server = ref.read(activeServerProvider);
    if (server == null) return;
    final api = ref.read(equipmentDiscoveryApiFactoryProvider)(server);
    try {
      await Future.wait([
        for (final t in equipmentProbeTypes)
          api
              .discover(t, forceRefresh: true)
              .catchError((Object _) => const <DiscoveredDevice>[]),
      ]);
    } finally {
      api.close();
    }
  }

  /// Sends one failed (or edited) row again, then re-reads once.
  Future<void> retry(String id) async {
    final row = _rows.where((r) => r.id == id).firstOrNull;
    if (row == null) return;
    if (row.status != FoundStatus.failed && row.status != FoundStatus.found) {
      return;
    }
    await _exclusive(() {
      _patch(id, (r) => r.copyWith(status: FoundStatus.waiting));
      return _sequence([id], checkLanded: true);
    });
  }

  /// Reads the bridge again and rebuilds the rows (ticks reset, secrets dropped).
  Future<void> redetect() => _exclusive(_redetect);

  Future<void> _redetect() async {
    ref.invalidate(bridgeAddressOverrideProvider);
    state = const AsyncLoading<List<FoundRow>>();
    try {
      _set(await _detect());
    } catch (e, st) {
      if (ref.mounted) state = AsyncError(e, st);
    }
  }

  /// By hand: one body to the bridge; on success the list is read again. Null while another
  /// operation runs (nothing sent).
  Future<AddOutcome?> addByHand(Map<String, Object?> body) => _exclusive(
    () async {
      final client = ref.read(alpacaBridgeSetupClientFactoryProvider)(_address);
      AddOutcome out;
      try {
        out = await client.addDevice(body);
      } catch (_) {
        out = const AddUnconfirmed();
      } finally {
        client.close();
      }
      await _reread();
      if (out is AddAdded) await _redetect();
      return out;
    },
  );

  var _opened = false;

  /// A panel was mounted: the first one uses the list read at build; a later one (another visit)
  /// reads the bridge again, since the kept list may be stale.
  void panelOpened() {
    if (_opened) redetect();
    _opened = true;
  }

  /// The drivers the catalog offered, for the by-hand list.
  List<BridgeDriver> get drivers => _drivers;

  /// The cameras a vendor has attached, for By hand's "Which camera?".
  Future<List<AttachedCamera>> camerasOf(String vendor) async {
    final client = ref.read(alpacaBridgeSetupClientFactoryProvider)(_address);
    try {
      return await client.cameras(vendor);
    } finally {
      client.close();
    }
  }
}
