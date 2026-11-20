import 'dart:async';

import 'package:flutter/foundation.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../services/profile_api.dart';
import 'profile_cache_state.dart';
import 'profile_management_state.dart';
import 'settings/migrated_sections.dart';
import 'settings/phd2_settings_state.dart';

/// #1644 — another device (or the daemon) wrote profile [section]
/// (`profile.updated`): read that section again so an open screen never shows
/// stale values, and refresh the offline profile cache. The write this device
/// made itself is skipped, so editing never resets its own screen. A section
/// no screen holds in memory needs nothing: it is read when opened. Custom
/// targets (`saved-targets`) are held but left out: syncing them is a separate
/// spec (#1644, Out of Scope).
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
// notifier holding that section. A section on the section module is read from
// `migratedSections`, so adding one there adds its row here.
final Map<String, _Hydrate> _hydrators = {
  for (final route in {for (final s in migratedSections) s.route})
    route: (c, api) => _hydrateMigrated(c, api, route),
  'phd2': (c, api) =>
      c.read(phd2SettingsProvider.notifier).hydrateFromServer(api),
};

Future<void> _hydrateMigrated(
  ProviderContainer c,
  ProfileApi api,
  String route,
) => Future.wait([
  for (final s in migratedSections)
    if (s.route == route) c.read(s.provider.notifier).hydrateFromServer(api),
]);
