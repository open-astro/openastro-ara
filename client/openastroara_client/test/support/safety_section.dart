import 'package:flutter_riverpod/misc.dart' show Override;
import 'package:openastroara/services/profile_api.dart';
import 'package:openastroara/state/settings/loaded_section.dart';
import 'package:openastroara/state/settings/safety_policies_state.dart';

/// The profile's safety policies for a widget test, with no HTTP (a Dio
/// request leaves a timer pending in a test that only pumps). The section
/// loads itself as in the app once a rig is there: [initial] is the rig's
/// copy, or null for a rig that does not answer. Every save that reached "the
/// rig" is in [puts]; [refuse] fails each save instead (nothing written).
class FakeSafetySection extends SafetyPoliciesNotifier {
  FakeSafetySection({this.initial = const SafetyPolicies()});

  SafetyPolicies? initial;
  final List<SafetyPolicies> puts = [];
  Object? refuse;

  @override
  Future<Tagged<SafetyPolicies>> fetch(ProfileApi api) async =>
      (value: initial ?? (throw StateError('rig unreachable')), etag: '"v1"');

  @override
  Future<Tagged<SafetyPolicies>> send(
    ProfileApi api,
    SafetyPolicies value,
    String? etag,
  ) async {
    if (refuse case final e?) throw e;
    puts.add(value);
    initial = value;
    return (value: value, etag: '"v${puts.length + 1}"');
  }
}

/// The policies as the rig's copy from the start (no load, no HTTP), for a
/// test that reads or edits them with no rig to load them from.
Override safetyFromRig([SafetyPolicies policies = const SafetyPolicies()]) =>
    safetyPoliciesProvider.overrideWith(() => _RigSafety(policies));

class _RigSafety extends SafetyPoliciesNotifier {
  _RigSafety(this.initial);
  final SafetyPolicies initial;
  @override
  LoadedSection<SafetyPolicies> build() =>
      LoadedSection(value: initial, source: SectionSource.rig, etag: '"v1"');
}
