import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/state/profile_management_state.dart';
import 'package:openastroara/state/settings/loaded_section.dart';
import 'package:openastroara/state/settings/polar_align_settings_state.dart';

import '../../support/fake_section_rig.dart';

// ADR 0015 for the polar-align section: the knobs that save on change save the
// rig's copy, with its tag, and nothing when the copy never loaded.

ProviderContainer _container(FakeSectionRig rig) {
  final c = ProviderContainer(
    overrides: [profileApiProvider.overrideWithValue(rig.api())],
  );
  addTearDown(c.dispose);
  c.listen(polarAlignSettingsProvider, (_, _) {});
  return c;
}

FakeSectionRig _rig() => FakeSectionRig({
  'polar-align': {...rigDefaults['polar-align']!, 'exposure_seconds': 4.0},
});

void main() {
  test('Polar align: a knob saves the rig copy with the change, sent with the loaded tag (ADR 0015)', () async {
    final rig = _rig();
    final c = _container(rig);
    await settle(() => c.read(polarAlignSettingsProvider).canSave);
    final etag = c.read(polarAlignSettingsProvider).etag;
    await c
        .read(polarAlignSettingsProvider.notifier)
        .saveKnob((s) => s.copyWith(seedRotationDeg: 15));
    final put = rig.puts.single;
    expect(put.ifMatch, etag);
    expect((put.body as Map)['seed_rotation_deg'], 15.0);
    expect(
      (put.body as Map)['exposure_seconds'],
      4.0,
      reason: 'the rest of the rig\'s copy goes back unchanged',
    );
  });

  test('Polar align: a knob with the load failed writes nothing and shows no default (ADR 0015)', () async {
    final rig = _rig()..failGet.add('polar-align');
    final c = _container(rig);
    await settle(() => c.read(polarAlignSettingsProvider).lastFailure != null);
    await expectLater(
      c
          .read(polarAlignSettingsProvider.notifier)
          .saveKnob((s) => s.copyWith(seedRotationDeg: 15)),
      throwsA(isA<SectionNotLoadedException>()),
    );
    expect(rig.puts, isEmpty);
    expect(
      c.read(polarAlignSettingsProvider).value,
      isNull,
      reason: 'no default shows',
    );
  });

  test('Polar align: a knob the rig does not save puts the rig copy back (ADR 0015)', () async {
    final rig = _rig()..failPut.add('polar-align');
    final c = _container(rig);
    await settle(() => c.read(polarAlignSettingsProvider).canSave);
    await expectLater(
      c
          .read(polarAlignSettingsProvider.notifier)
          .saveKnob((s) => s.copyWith(exposureSeconds: 30)),
      throwsA(anything),
    );
    expect(rig.puts, isEmpty);
    expect(c.read(polarAlignSettingsProvider).value!.exposureSeconds, 4.0);
  });
}
