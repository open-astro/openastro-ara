import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/services/profile_api.dart';
import 'package:openastroara/state/settings/safety_policies_state.dart';

import '../support/fake_section_rig.dart';

void main() {
  // #1185 — the on-disk-space-critical policy round-trips all three values; before the
  // `pause` option a server value of pause parsed as warn and the next save wrote warn back.
  test('on_disk_space_critical parses warn / pause / abort (any case) and serialises back', () {
    expect(ProfileApi.safetyPoliciesFromJson({'on_disk_space_critical': 'pause'}).onDiskSpaceCritical,
        DiskSpaceCriticalAction.pause);
    expect(ProfileApi.safetyPoliciesFromJson({'on_disk_space_critical': 'Abort'}).onDiskSpaceCritical,
        DiskSpaceCriticalAction.abort);
    expect(ProfileApi.safetyPoliciesFromJson({'on_disk_space_critical': 'warn'}).onDiskSpaceCritical,
        DiskSpaceCriticalAction.warn);
    expect(ProfileApi.safetyPoliciesFromJson(const {}).onDiskSpaceCritical, DiskSpaceCriticalAction.warn,
        reason: 'absent = the warn default');
    expect(ProfileApi.safetyPoliciesFromJson({'on_disk_space_critical': 'nonsense'}).onDiskSpaceCritical,
        DiskSpaceCriticalAction.warn, reason: 'an unknown value degrades to warn, like the server');

    final paused = ProfileApi.safetyPoliciesFromJson({'on_disk_space_critical': 'pause'});
    expect(ProfileApi.safetyPoliciesToJson(paused)['on_disk_space_critical'], 'pause');
  });

  // The wizard's write reads the section untagged and puts it back with its
  // own picks: the daemon's fault policies, which the client has no field
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
    final read = await api.getSafetyPolicies();
    await api.putSafetyPolicies(read.copyWith(meridianFlipAuto: false));
    final body = rig.puts.single.body as Map;
    expect(body['meridian_flip_auto'], isFalse);
    for (final e in faultPolicies.entries) {
      expect(body[e.key], e.value, reason: e.key);
    }
  });
}
