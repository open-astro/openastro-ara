import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/models/dome_settings.dart';
import 'package:openastroara/state/profile_management_state.dart';
import 'package:openastroara/state/settings/dome_settings_state.dart';
import 'package:openastroara/state/settings/loaded_section.dart';

import '../../support/fake_section_rig.dart';

(ProviderContainer, FakeSectionRig) _loaded() {
  final rig = FakeSectionRig({'dome': rigDefaults['dome']});
  final c = ProviderContainer(
    overrides: [profileApiProvider.overrideWithValue(rig.api())],
  );
  addTearDown(c.dispose);
  c.listen(domeSettingsProvider, (_, _) {});
  return (c, rig);
}

void main() {
  test('Dome settings: apply writes against the loaded tag and takes the '
      "rig's reply", () async {
    final (c, rig) = _loaded();
    await settle(() => c.read(domeSettingsProvider).canSave);
    final tag = rig.etagOf('dome');
    await c
        .read(domeSettingsProvider.notifier)
        .apply(const DomeSettings(domeRadiusMm: 1500));
    expect(rig.ifMatches, [tag]);
    expect(rig.puts.single.body, containsPair('dome_radius_mm', 1500));
    expect(c.read(domeSettingsProvider).value!.domeRadiusMm, 1500);
  });

  test('Dome settings: a refused save keeps the old geometry', () async {
    final (c, rig) = _loaded();
    await settle(() => c.read(domeSettingsProvider).canSave);
    rig.failPut.add('dome');
    await expectLater(
      c
          .read(domeSettingsProvider.notifier)
          .apply(const DomeSettings(domeRadiusMm: 1500)),
      throwsA(anything),
    );
    expect(c.read(domeSettingsProvider).value!.domeRadiusMm, 0);
  });

  test('Dome settings: a copy another device replaced is refused, nothing '
      'written', () async {
    final (c, rig) = _loaded();
    await settle(() => c.read(domeSettingsProvider).canSave);
    rig.savedElsewhere('dome');
    await expectLater(
      c
          .read(domeSettingsProvider.notifier)
          .apply(const DomeSettings(domeRadiusMm: 1500)),
      throwsA(isA<SectionConflictException>()),
    );
    expect(rig.puts, isEmpty);
  });

  test('Dome settings: no rig, nothing is loaded and apply throws', () async {
    final c = ProviderContainer(
      overrides: [profileApiProvider.overrideWithValue(null)],
    );
    addTearDown(c.dispose);
    expect(c.read(domeSettingsProvider).value, isNull);
    await expectLater(
      c
          .read(domeSettingsProvider.notifier)
          .apply(const DomeSettings(domeRadiusMm: 1500)),
      throwsA(isA<SectionNotLoadedException>()),
    );
  });
}
