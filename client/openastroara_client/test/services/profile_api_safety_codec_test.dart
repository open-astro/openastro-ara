import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/services/profile_api.dart';
import 'package:openastroara/state/settings/safety_policies_state.dart';

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
}
