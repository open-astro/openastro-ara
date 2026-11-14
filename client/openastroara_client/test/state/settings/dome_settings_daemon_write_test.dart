import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/models/discovered_device.dart';
import 'package:openastroara/models/dome_settings.dart';
import 'package:openastroara/models/dome_status.dart';
import 'package:openastroara/models/equipment_device_status.dart';
import 'package:openastroara/models/server.dart';
import 'package:openastroara/services/equipment_device_api.dart';
import 'package:openastroara/state/equipment/dome_state.dart';
import 'package:openastroara/state/profile_management_state.dart';
import 'package:openastroara/state/saved_server_state.dart';
import 'package:openastroara/state/settings/dome_settings_state.dart';

import '../../support/fake_section_rig.dart';

// ADR 0015 for a section the daemon also writes because of a control on the
// same panel: the dome's follow switch makes the daemon store follow_mount in
// the dome section (DomeFollower), outside the profile routes, so no
// profile.updated arrives. The next geometry edit on that panel must be sent
// against the copy the daemon now holds, not the one loaded before the switch,
// or every such edit is refused as a conflict.

const _section = 'dome';

class _Servers extends SavedServersNotifier {
  @override
  Future<List<AraServer>> build() async => const [
    AraServer(hostname: 'rig.test', port: 1),
  ];
}

/// The dome device: following on or off is the daemon writing the section.
class _Dome implements EquipmentDeviceClient<DomeStatus> {
  _Dome(this.rig);
  final FakeSectionRig rig;
  bool following = false;

  @override
  Future<DomeStatus?> getStatus() async => DomeStatus(
    deviceId: 'dome-0',
    name: 'Observatory',
    connectionState: EquipmentConnectionState.connected,
    capabilities: const DomeCapabilities(
      canSetShutter: true,
      canSetAzimuth: true,
      canSyncAzimuth: false,
      canPark: true,
      canFindHome: true,
    ),
    runtimeState: 'idle',
    azimuthDeg: 180,
    shutterOpen: true,
    atHome: false,
    parked: false,
    following: following,
  );

  @override
  Future<void> command(String subpath, [Map<String, dynamic>? body]) async {
    if (subpath != 'follow') return;
    following = body?['enabled'] == true;
    rig.daemonWrote(_section, {
      ...(rig.sectionOf(_section)! as Map<String, dynamic>),
      'follow_mount': following,
    });
  }

  @override
  Future<void> connect(DiscoveredDevice device) async {}
  @override
  Future<void> disconnect() async {}
  @override
  Future<void> forget() async {}
  @override
  void close() {}
}

void main() {
  test('Dome: a tolerance edit after the follow switch is sent against the '
      "daemon's new copy", () async {
    final rig = FakeSectionRig({
      _section: {
        ...const DomeSettings(domeRadiusMm: 1500).toJson(),
        'follow_mount': false,
      },
    });
    final dome = _Dome(rig);
    final c = ProviderContainer(
      overrides: [
        savedServersProvider.overrideWith(_Servers.new),
        profileApiProvider.overrideWithValue(rig.api()),
        domeApiFactoryProvider.overrideWithValue((_) => dome),
      ],
    );
    addTearDown(c.dispose);
    c.listen(domeProvider, (_, _) {});
    c.listen(domeSettingsProvider, (_, _) {});
    await c.read(savedServersProvider.future);
    await c.read(domeProvider.future);
    final loaded = await c.read(domeSettingsProvider.future);

    await c.read(domeProvider.notifier).followMount(true);
    await Future<void>.delayed(Duration.zero);
    final tagAfterFollow = rig.etagOf(_section);
    try {
      await c
          .read(domeSettingsProvider.notifier)
          .save(loaded.copyWith(azimuthToleranceDegrees: 4));
    } catch (_) {}

    expect(rig.ifMatches, [tagAfterFollow]);
    expect(rig.puts, hasLength(1));
  });
}
