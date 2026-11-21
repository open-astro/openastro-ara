import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/models/equipment_device_status.dart';
import 'package:openastroara/models/mount_status.dart';
import 'package:openastroara/services/equipment_device_api.dart';
import 'package:openastroara/state/equipment/mount_state.dart';

// The rig, 2026-11-02: an EQMOD mount at home with tracking off refused every
// GoTo ("SlewToCoordinatesAsync requires Tracking to be true", the daemon's
// log), so nothing moved.

class _Api implements EquipmentDeviceClient<MountStatus> {
  _Api(this.status);
  final MountStatus status;
  final calls = <String>[];
  @override
  Future<MountStatus?> getStatus() async => status;
  @override
  Future<void> command(String subpath, [Map<String, dynamic>? body]) async =>
      calls.add(subpath == 'tracking' ? 'tracking ${body?['enabled']}' : subpath);
  @override
  dynamic noSuchMethod(Invocation invocation) => super.noSuchMethod(invocation);
}

MountStatus _mount({bool tracking = false, bool canSetTracking = true}) => MountStatus(
      deviceId: 'm',
      name: 'EQM-35',
      connectionState: EquipmentConnectionState.connected,
      capabilities: MountCapabilities(
        canSlew: true,
        canSync: true,
        canPark: true,
        canUnpark: true,
        canSetTracking: canSetTracking,
        canFindHome: true,
        canMoveAxis: false,
        axisRatesDegPerSec: const [],
        axisRateBands: const [],
      ),
      runtimeState: 'idle',
      rightAscensionHours: 20.5,
      declinationDegrees: -90,
      tracking: tracking,
      parked: false,
      atHome: true,
    );

Future<List<String>> _slew(MountStatus status, {bool sync = false}) async {
  final api = _Api(status);
  final c = ProviderContainer(overrides: [mountApiProvider.overrideWithValue(api)]);
  addTearDown(c.dispose);
  await c.read(mountProvider.future);
  await c.read(mountProvider.notifier).slewTo(1.0, -60, sync: sync);
  return api.calls;
}

void main() {
  test('a GoTo with tracking off turns tracking on first', () async {
    expect(await _slew(_mount()), ['tracking true', 'slew']);
  });
  test('tracking already on: just the slew', () async {
    expect(await _slew(_mount(tracking: true)), ['slew']);
  });
  test('a sync never touches tracking', () async {
    expect(await _slew(_mount(), sync: true), ['slew']);
  });
  test('a mount that cannot set tracking: just the slew', () async {
    expect(await _slew(_mount(canSetTracking: false)), ['slew']);
  });
}
