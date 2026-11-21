import 'package:flutter_test/flutter_test.dart';

import '../support/fake_section_rig.dart';

void main() {
  test('the polar-align section reads east_direction, an older rig without it is by hemisphere, and a save sends it (#2081)', () async {
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

    final older = FakeSectionRig({'polar-align': rigDefaults['polar-align']!});
    expect(
      (await older.api().getPolarAlignSection()).value.eastDirection,
      isNull,
    );
  });
}
