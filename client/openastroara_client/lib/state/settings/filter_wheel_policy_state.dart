import 'package:flutter_riverpod/flutter_riverpod.dart';

import 'settings_sync_mixin.dart';

import '../../services/profile_api.dart';
import '../saved_server_state.dart';

/// #1075 — the profile's filter-wheel policy, daemon-backed
/// (`/api/v1/profile/filter-wheel/policy`): whether Ara parks the wheel on
/// slot 1 (L) the first time it connects after the daemon starts (#1066).
/// Same shape as [EquipmentConnectionNotifier]: one-shot hydrate on build,
/// optimistic local update + fire-and-forget PUT on change, silent failure
/// (best-effort, trusted LAN).
class FilterWheelPolicy {
  final bool homeOnFirstConnect;
  const FilterWheelPolicy({this.homeOnFirstConnect = true});

  factory FilterWheelPolicy.fromJson(Map<String, dynamic> json) =>
      FilterWheelPolicy(
        homeOnFirstConnect: (json['home_on_first_connect'] as bool?) ?? true,
      );

  Map<String, dynamic> toJson() => {'home_on_first_connect': homeOnFirstConnect};

  FilterWheelPolicy copyWith({bool? homeOnFirstConnect}) => FilterWheelPolicy(
        homeOnFirstConnect: homeOnFirstConnect ?? this.homeOnFirstConnect,
      );

  @override
  bool operator ==(Object other) =>
      other is FilterWheelPolicy &&
      other.homeOnFirstConnect == homeOnFirstConnect;

  @override
  int get hashCode => homeOnFirstConnect.hashCode;
}

class FilterWheelPolicyNotifier extends Notifier<FilterWheelPolicy>
    with SettingsSyncMixin<FilterWheelPolicy> {
  @override
  FilterWheelPolicy build() {
    Future.microtask(_tryHydrate);
    return const FilterWheelPolicy();
  }

  void setHomeOnFirstConnect(bool v) {
    state = state.copyWith(homeOnFirstConnect: v);
    Future.microtask(_tryPersist);
  }

  /// Manual hydrate — exposed for tests that inject a mocked api.
  Future<void> hydrateFromServer(ProfileApi api) =>
      hydrateGuarded(() => api.getFilterWheelPolicy());

  /// Manual persist — exposed for tests.
  Future<FilterWheelPolicy> persistToServer(ProfileApi api) =>
      persistGuarded((sent) => api.putFilterWheelPolicy(sent));

  ProfileApi? _activeApi() {
    final server = ref.read(activeServerProvider);
    return server == null ? null : ProfileApi(server);
  }

  Future<void> _tryHydrate() async {
    final api = _activeApi();
    if (api == null) return;
    try {
      await hydrateGuarded(() => api.getFilterWheelPolicy());
    } catch (_) {
      // Silent — the default (home on) remains.
    }
  }

  Future<void> _tryPersist() async {
    final api = _activeApi();
    if (api == null) return;
    try {
      await persistGuarded((sent) => api.putFilterWheelPolicy(sent));
    } catch (_) {
      // Silent — the local optimistic update stays.
    }
  }
}

final filterWheelPolicyProvider =
    NotifierProvider<FilterWheelPolicyNotifier, FilterWheelPolicy>(
  FilterWheelPolicyNotifier.new,
);
