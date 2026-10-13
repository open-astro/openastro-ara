import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/models/equipment_device_status.dart';
import 'package:openastroara/models/mount_spec_sheet.dart';
import 'package:openastroara/models/mount_status.dart';
import 'package:openastroara/models/server.dart';
import 'package:openastroara/models/tracking_calibration.dart';
import 'package:openastroara/services/tracking_api.dart';
import 'package:openastroara/state/equipment/mount_state.dart';
import 'package:openastroara/state/saved_server_state.dart';
import 'package:openastroara/state/tracking/tracking_calibration_state.dart';
import 'package:openastroara/state/ws/ws_providers.dart';

/// #1264 stage 1 — the advisor's prior is re-read when the daemon's answer can
/// change: a mount (with a sheet) connecting after the editor opened.
class _CountingApi extends TrackingApi {
  _CountingApi() : super(const AraServer(hostname: 'x', port: 1));
  int priorReads = 0;
  TrackingPrior answer = const TrackingPrior();
  @override
  Future<TrackingCalibration?> read() async => null;
  @override
  Future<TrackingPrior> readPrior() async {
    priorReads++;
    return answer;
  }

  @override
  void close() {}
}

class _Mount extends MountNotifier {
  _Mount(this._v);
  final MountStatus? _v;
  @override
  Future<MountStatus?> build() async => _v;
  void set(MountStatus? v) => state = AsyncData(v);
}

MountStatus _mount(MountSpecSheet? specs) => MountStatus(
  deviceId: 'mnt',
  name: 'EQM-35',
  connectionState: EquipmentConnectionState.connected,
  capabilities: MountCapabilities(
    canSlew: true,
    canSync: true,
    canPark: true,
    canUnpark: true,
    canSetTracking: true,
    canFindHome: false,
    specs: specs,
  ),
  runtimeState: 'tracking',
  rightAscensionHours: 5,
  declinationDegrees: 20,
  tracking: true,
  parked: false,
  atHome: false,
);

void main() {
  test('a mount connecting with a spec sheet re-reads the prior; an unrelated status tick does not', () async {
    final api = _CountingApi();
    final mount = _Mount(null);
    final c = ProviderContainer(
      overrides: [
        activeServerProvider.overrideWithValue(
          const AraServer(hostname: 'x', port: 1),
        ),
        trackingApiFactoryProvider.overrideWithValue((_) => api),
        wsEventsProvider.overrideWith((ref) => const Stream.empty()),
        mountProvider.overrideWith(() => mount),
      ],
    );
    addTearDown(c.dispose);
    c.listen(trackingPriorProvider, (_, _) {});
    expect((await c.read(trackingPriorProvider.future)).seconds, isNull);
    expect(api.priorReads, 1);

    // The mount connects and the bridge ships a sheet: the daemon now resolves a prior.
    api.answer = const TrackingPrior(
      seconds: 13.8,
      source: "the mount's spec sheet (manufacturer)",
    );
    mount.set(
      _mount(
        const MountSpecSheet(
          model: 'EQM-35 Pro',
          wormPeriodS: 638,
          periodicErrorPpArcsec: 25,
        ),
      ),
    );
    await Future<void>.delayed(Duration.zero);
    expect((await c.read(trackingPriorProvider.future)).seconds, 13.8);
    expect(api.priorReads, 2);

    // The same sheet on a later status tick (RA moved) is not a reason to re-read.
    mount.set(
      _mount(
        const MountSpecSheet(
          model: 'EQM-35 Pro',
          wormPeriodS: 638,
          periodicErrorPpArcsec: 25,
        ),
      ),
    );
    await Future<void>.delayed(Duration.zero);
    await c.read(trackingPriorProvider.future);
    expect(
      api.priorReads,
      2,
      reason: 'select() on the sheet: equal sheets do not rebuild',
    );
  });
}
