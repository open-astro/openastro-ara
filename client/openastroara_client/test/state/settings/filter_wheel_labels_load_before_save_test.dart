import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/models/discovered_device.dart';
import 'package:openastroara/models/equipment_device_status.dart';
import 'package:openastroara/models/filter_wheel_status.dart';
import 'package:openastroara/models/server.dart';
import 'package:openastroara/services/equipment_device_api.dart';
import 'package:openastroara/state/equipment/filter_wheel_state.dart';
import 'package:openastroara/state/profile_management_state.dart';
import 'package:openastroara/state/saved_server_state.dart';
import 'package:openastroara/state/settings/loaded_section.dart';
import 'package:openastroara/state/settings/filter_wheel_labels_state.dart';

import '../../support/fake_section_rig.dart';

// ADR 0015 for an equipment section: the filter wheel's slot labels. The
// notifier loads itself from profileApiProvider and keeps the reference-8
// labels when that load fails, so the next slot edit writes those defaults
// over every other slot the person named on the rig. Assertions are on what
// reached the rig only.

const _section = 'filter-wheel/labels';

void main() {
  late FakeSectionRig rig;
  late ProviderContainer c;

  setUp(() {
    rig = FakeSectionRig({
      _section: {
        'labels': ['Lum', 'Red', 'Green', 'Blue', 'Ha', 'OIII', 'SII', 'Dark'],
      },
    });
    c = ProviderContainer(
      overrides: [profileApiProvider.overrideWithValue(rig.api())],
    );
    addTearDown(c.dispose);
  });

  test(
    'Filter wheel labels: a slot edit after a failed load writes nothing',
    () async {
      rig.failGet.add(_section);
      c.read(filterWheelLabelsProvider);
      // The notifier loads on its own: let that load fail.
      await Future<void>.delayed(Duration.zero);
      await Future<void>.delayed(Duration.zero);
      final n = c.read(filterWheelLabelsProvider.notifier);
      n.setLabel(1, 'L-eXtreme');
      try {
        await n.persistToServer();
      } catch (_) {}
      expect(rig.puts, isEmpty);
    },
  );

  test('Filter wheel labels load again when the wheel reports its slots, '
      'not when it connects', () async {
    final wheel = _Wheel();
    c.dispose();
    c = ProviderContainer(
      overrides: [
        profileApiProvider.overrideWithValue(rig.api()),
        filterWheelApiFactoryProvider.overrideWithValue((_) => wheel),
        savedServersProvider.overrideWith(_Servers.new),
      ],
    );
    c.listen(filterWheelProvider, (_, _) {});
    c.listen(filterWheelLabelsProvider, (_, _) {});
    await c.read(savedServersProvider.future);
    await c.read(filterWheelProvider.future);
    final n = c.read(filterWheelLabelsProvider.notifier);
    await settle(() => c.read(filterWheelLabelsProvider).canSave);

    // Connected, no slots yet: the daemon has not mirrored the names.
    expect(c.read(filterWheelLabelsProvider).source, SectionSource.rig);
    rig.daemonWrote(_section, {
      'labels': ['A', 'B'],
    });
    final mirroredTag = rig.etagOf(_section);

    // The wheel now reports its slots (MirrorSlotLabels has written them).
    wheel.slots = ['A', 'B'];
    await c.read(filterWheelProvider.notifier).refresh();
    await settle(
      () => c.read(filterWheelLabelsProvider).value?.labelAt(1) == 'A',
    );

    n.setLabel(1, 'L-eXtreme');
    await n.persistToServer();
    expect(rig.ifMatches, [mirroredTag]);
    expect(rig.puts, hasLength(1));
  });
}

class _Servers extends SavedServersNotifier {
  @override
  Future<List<AraServer>> build() async => const [
    AraServer(hostname: 'rig.test', port: 1),
  ];
}

/// A connected wheel whose reported slots a test sets.
class _Wheel implements EquipmentDeviceClient<FilterWheelStatus> {
  List<String> slots = const [];

  @override
  Future<FilterWheelStatus?> getStatus() async => FilterWheelStatus(
    deviceId: 'fw-0',
    name: 'Wheel',
    connectionState: EquipmentConnectionState.connected,
    runtimeState: 'idle',
    currentSlot: 0,
    slots: [
      for (var i = 0; i < slots.length; i++)
        FilterSlot(position: i, name: slots[i], focusOffset: 0),
    ],
    manual: false,
  );

  @override
  Future<void> command(String subpath, [Map<String, dynamic>? body]) async {}
  @override
  Future<void> connect(DiscoveredDevice device) async {}
  @override
  Future<void> disconnect() async {}
  @override
  Future<void> forget() async {}
  @override
  void close() {}
}
