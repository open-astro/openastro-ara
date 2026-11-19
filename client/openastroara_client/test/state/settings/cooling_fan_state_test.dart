import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/models/switch_device.dart' show CoolingFan;
import 'package:openastroara/state/profile_management_state.dart';
import 'package:openastroara/state/settings/cooling_fan_state.dart';
import 'package:openastroara/state/settings/loaded_section.dart';

import '../../support/fake_section_rig.dart';

const _fan = CoolingFan(deviceId: 'box', portId: 1);

(ProviderContainer, FakeSectionRig) _loaded({Object? body}) {
  final rig = FakeSectionRig({
    'cooling-fan': body ?? rigDefaults['cooling-fan'],
  });
  final c = ProviderContainer(
    overrides: [profileApiProvider.overrideWithValue(rig.api())],
  );
  addTearDown(c.dispose);
  c.listen(coolingFanProvider, (_, _) {});
  return (c, rig);
}

void main() {
  test('Cooling fan: the rig\'s copy loads, a null device is "none"', () async {
    final (c, _) = _loaded();
    await settle(() => c.read(coolingFanProvider).canSave);
    expect(c.read(coolingFanProvider).value, CoolingFan.none);
  });

  test('Cooling fan: choose writes against the loaded tag and takes the '
      'rig\'s reply', () async {
    final (c, rig) = _loaded();
    await settle(() => c.read(coolingFanProvider).canSave);
    final tag = rig.etagOf('cooling-fan');
    await c.read(coolingFanProvider.notifier).choose(_fan);
    expect(rig.ifMatches, [tag]);
    expect(rig.puts.single.body, {'device_id': 'box', 'port_id': 1});
    expect(c.read(coolingFanProvider).value, _fan);
  });

  test('Cooling fan: a refused save keeps the old choice', () async {
    final (c, rig) = _loaded(body: {'device_id': 'old', 'port_id': 0});
    await settle(() => c.read(coolingFanProvider).canSave);
    rig.failPut.add('cooling-fan');
    await expectLater(
      c.read(coolingFanProvider.notifier).choose(_fan),
      throwsA(anything),
    );
    expect(
      c.read(coolingFanProvider).value,
      const CoolingFan(deviceId: 'old', portId: 0),
    );
  });

  test('Cooling fan: a copy another device replaced is refused, nothing '
      'written', () async {
    final (c, rig) = _loaded();
    await settle(() => c.read(coolingFanProvider).canSave);
    rig.savedElsewhere('cooling-fan');
    await expectLater(
      c.read(coolingFanProvider.notifier).choose(_fan),
      throwsA(isA<SectionConflictException>()),
    );
    expect(rig.puts, isEmpty);
    expect(c.read(coolingFanProvider).conflict, isTrue);
    expect(c.read(coolingFanProvider).value, _fan); // kept for Reload
  });

  test('Cooling fan: no rig, nothing is loaded and choose throws', () async {
    final c = ProviderContainer(
      overrides: [profileApiProvider.overrideWithValue(null)],
    );
    addTearDown(c.dispose);
    expect(c.read(coolingFanProvider).value, isNull);
    await expectLater(
      c.read(coolingFanProvider.notifier).choose(_fan),
      throwsA(isA<SectionNotLoadedException>()),
    );
  });
}
