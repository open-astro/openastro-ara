import 'dart:async';

import 'package:flutter/foundation.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../services/profile_api.dart';
import 'profile_cache_state.dart';
import 'profile_management_state.dart';
import 'settings/migrated_sections.dart';

/// #1644 — another device (or the daemon) wrote profile [section]
/// (`profile.updated`): read that section again so an open screen never shows
/// stale values, and refresh the offline profile cache. The write this device
/// made itself is skipped, so editing never resets its own screen. A section
/// on the section module is not skipped by time: it takes the rig's copy only
/// when the tag moved past the one it holds, so its own save's announcement
/// changes nothing and a daemon write in the same moments (the guide camera
/// fill on guider connect, ADR 0015) still lands. A section
/// no screen holds in memory needs nothing: it is read when opened. Custom
/// targets (`saved-targets`) are held but left out: syncing them is a separate
/// spec (#1644, Out of Scope).
Future<void> applyProfileUpdated(
  ProviderContainer container,
  String section,
) async {
  final api = container.read(profileApiProvider);
  if (api == null) return;
  final own = ProfileApi.wroteRecently(section);
  final migrated = _migratedRoutes.contains(section);
  if (own && !migrated) return;
  try {
    if (migrated) await _syncMigrated(container, api, section);
    if (own) return; // the cache already holds what this device wrote
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

// Wire section names (first path segment under /api/v1/profile/) held in
// memory: every one is on the section module, so its route comes from
// `migratedSections` and it syncs by tag.
final Set<String> _migratedRoutes = {for (final s in migratedSections) s.route};

Future<void> _syncMigrated(
  ProviderContainer c,
  ProfileApi api,
  String route,
) => Future.wait([
  for (final s in migratedSections)
    if (s.route == route) c.read(s.provider.notifier).syncFromServer(api),
]);
