import 'package:flutter_riverpod/flutter_riverpod.dart';

import 'loaded_section.dart';

import '../../services/profile_api.dart';

/// #1075 — the profile's filter-wheel policy, daemon-backed
/// (`/api/v1/profile/filter-wheel/policy`): whether Ara parks the wheel on
/// slot 0 (L) the first time it connects after the daemon starts (#1066).
/// Same shape as [EquipmentConnectionNotifier]: loads itself, saves on change.
/// ADR 0015: no value until the rig answered (no "home on" default).
class FilterWheelPolicy {
  final bool homeOnFirstConnect;

  /// #1667 — on a filter change, move the focuser by the two filters' offset difference.
  final bool useFilterWheelOffsets;
  // No defaults: the daemon's normalizer is the one owner (ADR 0012, ADR
  // 0015 rule 3), so a value is the rig's copy or the offline cache's.
  const FilterWheelPolicy({
    required this.homeOnFirstConnect,
    required this.useFilterWheelOffsets,
  });

  Map<String, dynamic> toJson() => {
        'home_on_first_connect': homeOnFirstConnect,
        'use_filter_wheel_offsets': useFilterWheelOffsets,
      };

  FilterWheelPolicy copyWith({bool? homeOnFirstConnect, bool? useFilterWheelOffsets}) => FilterWheelPolicy(
        homeOnFirstConnect: homeOnFirstConnect ?? this.homeOnFirstConnect,
        useFilterWheelOffsets: useFilterWheelOffsets ?? this.useFilterWheelOffsets,
      );

  @override
  bool operator ==(Object other) =>
      other is FilterWheelPolicy &&
      other.homeOnFirstConnect == homeOnFirstConnect &&
      other.useFilterWheelOffsets == useFilterWheelOffsets;

  @override
  int get hashCode => Object.hash(homeOnFirstConnect, useFilterWheelOffsets);
}

class FilterWheelPolicyNotifier extends SelfLoadingSection<FilterWheelPolicy> {
  /// Each switch saves on change (the panel shows a refusal). A failed save
  /// puts the rig's copy back; a 412 keeps the edit (ADR 0015 rule 6).
  Future<void> setHomeOnFirstConnect(bool v) =>
      _save((c) => c.copyWith(homeOnFirstConnect: v));

  Future<void> setUseFilterWheelOffsets(bool v) =>
      _save((c) => c.copyWith(useFilterWheelOffsets: v));

  Future<void> _save(FilterWheelPolicy Function(FilterWheelPolicy c) change) {
    final v = state.value;
    return v == null ? editAndSave(change) : replaceAndSave(change(v));
  }

  @override
  Future<Tagged<FilterWheelPolicy>> fetch(ProfileApi api) =>
      api.getFilterWheelPolicySection();

  @override
  Future<Tagged<FilterWheelPolicy>> send(
    ProfileApi api,
    FilterWheelPolicy value,
    String? etag,
  ) => api.putFilterWheelPolicySection(value, ifMatch: etag);
}

final filterWheelPolicyProvider =
    NotifierProvider<
      FilterWheelPolicyNotifier,
      LoadedSection<FilterWheelPolicy>
    >(FilterWheelPolicyNotifier.new);
