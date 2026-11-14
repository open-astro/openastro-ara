import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/state/profile_management_state.dart';
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
}
