import 'package:flutter_test/flutter_test.dart';

import '../support/fake_section_rig.dart';

void main() {
  test('the polar-align section reads east_direction, a rig reply without the key fails the read, and a save sends it (#2081)', () async {
    final rig = FakeSectionRig({
      'polar-align': {...rigDefaults['polar-align']!, 'east_direction': false},
    });
    final api = rig.api();
    final west = await api.getPolarAlignSection();
    expect(west.value.eastDirection, isFalse);

    await api.putPolarAlignSection(
      west.value.withEastDirection(null),
      ifMatch: west.etag,
    );
    final sent = rig.puts.single.body! as Map;
    expect(sent.containsKey('east_direction'), isTrue);
    expect(sent['east_direction'], isNull);

    // Strict like the other keys (#1807): a missing key is not "by hemisphere".
    final older = FakeSectionRig({
      'polar-align': {...rigDefaults['polar-align']!}..remove('east_direction'),
    });
    await expectLater(
      older.api().getPolarAlignSection(),
      throwsA(isA<FormatException>()),
    );
  });

  test('a profile that still carries check_step_deg and stop_tracking_when_done reads, and a save sends neither', () async {
    final rig = FakeSectionRig({
      'polar-align': {
        ...rigDefaults['polar-align']!,
        'check_step_deg': 15.0,
        'stop_tracking_when_done': false,
        'seed_rotation_deg': 20.0,
      },
    });
    final api = rig.api();
    final read = await api.getPolarAlignSection();
    expect(read.value.seedRotationDeg, 20.0);

    await api.putPolarAlignSection(read.value, ifMatch: read.etag);
    final sent = rig.puts.single.body! as Map;
    expect(sent.containsKey('check_step_deg'), isFalse);
    expect(sent.containsKey('stop_tracking_when_done'), isFalse);
    expect(sent['seed_rotation_deg'], 20.0);
  });

  test('a profile from the rig without check_step_deg and stop_tracking_when_done reads', () async {
    final read = await FakeSectionRig({'polar-align': {...rigDefaults['polar-align']!}}).api().getPolarAlignSection();
    expect(read.value.loopMode, 'loop');
    expect(read.value.toJson().keys, isNot(contains('check_step_deg')));
  });
}
