import 'dart:async';

import 'package:flutter/foundation.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../services/profile_api.dart';
import 'profile_cache_state.dart';
import 'profile_management_state.dart';
import 'settings/autofocus_settings_state.dart';
import 'settings/camera_electronics_state.dart';
import 'settings/custom_horizon_state.dart';
import 'settings/diagnostics_mode_state.dart';
import 'settings/equipment_connection_state.dart';
import 'settings/filenames_settings_state.dart';
import 'settings/filter_set_state.dart';
import 'settings/filter_wheel_policy_state.dart';
import 'settings/imaging_defaults_state.dart';
import 'settings/notifications_settings_state.dart';
import 'settings/optics_settings_state.dart';
import 'settings/phd2_settings_state.dart';
import 'settings/plate_solve_settings_state.dart';
import 'settings/safety_policies_state.dart';
import 'settings/site_settings_state.dart';
import 'settings/storage_settings_state.dart';
import 'settings/unguided_settings_state.dart';

/// #1644 — another device (or the daemon) wrote profile [section]
/// (`profile.updated`): read that section again so an open screen never shows
/// stale values, and refresh the offline profile cache. The write this device
/// made itself is skipped, so editing never resets its own screen. A section
/// no screen holds in memory needs nothing: it is read when opened.
Future<void> applyProfileUpdated(
  ProviderContainer container,
  String section,
) async {
  final api = container.read(profileApiProvider);
  if (api == null) return;
  if (ProfileApi.wroteRecently(section)) return;
  try {
    final hydrate = _hydrators[section];
    if (hydrate != null) await hydrate(container, api);
    final list = await api.listProfiles();
    await captureProfileCache(
      container.read(profileCacheServiceProvider),
      api,
      list,
    );
    container.invalidate(cachedProfilesProvider);
  } catch (e) {
    debugPrint('[profile-live-sync] $section refresh failed: $e');
  }
}

typedef _Hydrate = Future<void> Function(ProviderContainer c, ProfileApi api);

// Wire section name (first path segment under /api/v1/profile/) → the
// notifier holding that section.
final Map<String, _Hydrate> _hydrators = {
  'imaging-defaults': (c, api) =>
      c.read(imagingDefaultsProvider.notifier).hydrateFromServer(api),
  'autofocus': (c, api) =>
      c.read(autofocusSettingsProvider.notifier).hydrateFromServer(api),
  'unguided': (c, api) =>
      c.read(unguidedSettingsProvider.notifier).hydrateFromServer(api),
  'filter-set': (c, api) =>
      c.read(filterSetProvider.notifier).hydrateFromServer(api),
  'filter-wheel': (c, api) =>
      c.read(filterWheelPolicyProvider.notifier).hydrateFromServer(api),
  'camera-electronics': (c, api) =>
      c.read(cameraElectronicsProvider.notifier).hydrateFromServer(api),
  'optics': (c, api) =>
      c.read(opticsSettingsProvider.notifier).hydrateFromServer(api),
  'site': (c, api) =>
      c.read(siteSettingsProvider.notifier).hydrateFromServer(api),
  'custom-horizon': (c, api) =>
      c.read(customHorizonProvider.notifier).hydrateFromServer(api),
  'safety': (c, api) =>
      c.read(safetyPoliciesProvider.notifier).hydrateFromServer(api),
  'storage': (c, api) =>
      c.read(storageSettingsProvider.notifier).hydrateFromServer(api),
  'filenames': (c, api) =>
      c.read(filenamesSettingsProvider.notifier).hydrateFromServer(api),
  'plate-solve': (c, api) =>
      c.read(plateSolveSettingsProvider.notifier).hydrateFromServer(api),
  'phd2': (c, api) =>
      c.read(phd2SettingsProvider.notifier).hydrateFromServer(api),
  'notifications': (c, api) =>
      c.read(notificationsSettingsProvider.notifier).hydrateFromServer(api),
  'diagnostics-mode': (c, api) =>
      c.read(diagnosticsModeProvider.notifier).hydrateFromServer(api),
  'equipment-connection': (c, api) =>
      c.read(equipmentConnectionProvider.notifier).hydrateFromServer(api),
};
