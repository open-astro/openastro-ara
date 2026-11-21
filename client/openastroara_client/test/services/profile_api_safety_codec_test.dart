import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/services/profile_api.dart';
import 'package:openastroara/state/settings/safety_policies_state.dart';

import '../support/fake_section_rig.dart';

void main() {
  // #1185 — the on-disk-space-critical policy round-trips all three values; before the
  // `pause` option a server value of pause parsed as warn and the next save wrote warn back.
  test('on_disk_space_critical parses warn / pause / abort (any case) and serialises back', () {
    SafetyPolicies read(String v) =>
        ProfileApi.safetyPoliciesFromJson({...rigDefaults['safety']!, 'on_disk_space_critical': v});
    expect(read('pause').onDiskSpaceCritical, DiskSpaceCriticalAction.pause);
    expect(read('Abort').onDiskSpaceCritical, DiskSpaceCriticalAction.abort);
    expect(read('warn').onDiskSpaceCritical, DiskSpaceCriticalAction.warn);
    expect(ProfileApi.safetyPoliciesToJson(read('pause'))['on_disk_space_critical'], 'pause');
  });

  // The keys a reply holds beyond the client's fields go back on a Save as
  // served; the list of the client's own is written out (no default policies
  // object to take it from, ADR 0015), so it must match what a Save writes.
  test('safetyModeledKeys names every key safetyPoliciesToJson writes', () {
    final read = ProfileApi.safetyPoliciesFromJson({...rigDefaults['safety']!});
    expect(read.unmodeled.keys.toSet(), {
      'hot_reconnect_enabled',
      'on_camera_lost',
      'on_mount_lost',
      'on_tracking_lost',
      'switch_value_tolerance_pct',
    });
    final written = ProfileApi.safetyPoliciesToJson(read.copyWith(unmodeled: const {}));
    expect(written.keys.toSet(), ProfileApi.safetyModeledKeys);
  });

  // The wizard's write reads the section with its tag and puts it back with
  // its own picks: the daemon's fault policies, which the client has no field
  // for, go back as the rig had them, not as the daemon's defaults.
  test("the wizard's safety write keeps the rig's fault policies", () async {
    const faultPolicies = {
      'hot_reconnect_enabled': false,
      'on_camera_lost': 'pause',
      'on_mount_lost': 'notify_only',
      'on_tracking_lost': 'pause',
      'switch_value_tolerance_pct': 12.5,
    };
    final rig = FakeSectionRig({
      'safety': {...rigDefaults['safety']!, ...faultPolicies},
    });
    final api = rig.api();
    final read = await api.getSafetySection();
    await api.putSafetySection(
      read.value.copyWith(meridianFlipAuto: false),
      ifMatch: read.etag,
    );
    final body = rig.puts.single.body as Map;
    expect(body['meridian_flip_auto'], isFalse);
    for (final e in faultPolicies.entries) {
      expect(body[e.key], e.value, reason: e.key);
    }
  });
}
