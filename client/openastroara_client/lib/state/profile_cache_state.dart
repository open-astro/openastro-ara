import 'package:flutter/foundation.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../models/profile_list.dart';
import '../services/profile_api.dart';
import '../services/profile_cache_service.dart';
import 'settings/autofocus_settings_state.dart';
import 'settings/unguided_settings_state.dart';
import 'settings/camera_electronics_state.dart';
import 'settings/custom_horizon_state.dart';
import 'settings/filter_set_state.dart';
import 'settings/imaging_defaults_state.dart';
import 'settings/optics_settings_state.dart';
import 'settings/site_settings_state.dart';

final profileCacheServiceProvider =
    Provider<ProfileCacheService>((ref) => ProfileCacheService());

/// One profile as the offline launch box sees it: the cached identity plus
/// whether a gear snapshot exists to seed planning with.
@immutable
class CachedProfile {
  final String id;
  final String name;
  final bool hasSections;

  /// When the gear snapshot was captured (null when [hasSections] is false or
  /// the snapshot predates the stamp) — the offline picker shows this so a
  /// stale ranking looks stale.
  final DateTime? capturedUtc;
  const CachedProfile(
      {required this.id,
      required this.name,
      required this.hasSections,
      this.capturedUtc});
}

@immutable
class CachedProfileList {
  final String? activeId;
  final List<CachedProfile> profiles;
  const CachedProfileList({required this.activeId, required this.profiles});
}

/// The locally-cached profiles (empty when this machine never connected to a
/// daemon). Invalidate after a capture to refresh consumers.
final cachedProfilesProvider = FutureProvider<CachedProfileList>((ref) async {
  final cache = await ref.watch(profileCacheServiceProvider).load();
  final sections = cache['sections'];
  final raw = cache['profiles'];
  return CachedProfileList(
    activeId: cache['active_id']?.toString(),
    profiles: [
      if (raw is List)
        for (final p in raw)
          if (p is Map && p['id'] is String)
            CachedProfile(
              id: p['id'] as String,
              name: p['name']?.toString() ?? '',
              hasSections: sections is Map && sections[p['id']] is Map,
              capturedUtc: sections is Map && sections[p['id']] is Map
                  ? DateTime.tryParse(
                      (sections[p['id']] as Map)['captured_utc']?.toString() ??
                          '')?.toUtc()
                  : null,
            ),
    ],
  );
});

/// Capture the daemon's profile list + the ACTIVE profile's planning-relevant
/// sections into the local cache. Best-effort and non-blocking by design: a
/// failure just means the offline cache is staler, never an error surface.
/// Sections are stored in wire shape via the [ProfileApi] codecs, so the
/// offline seed decodes with the exact same logic the live client uses.
Future<void> captureProfileCache(
    ProfileCacheService cache, ProfileApi api, ProfileList list) async {
  try {
    await cache.saveList(list.activeId, [
      for (final p in list.profiles) (id: p.id, name: p.name),
    ]);
    final activeId = list.activeId;
    if (activeId == null) return;
    // The section GETs read the ACTIVE profile — that's the one we snapshot.
    // Parallel (like _hydratePlanningSettings): this fires on every list load
    // AND after every mutation, so serializing 6 round-trips adds up.
    final (optics, imaging, autofocus, site, filters, electronics, horizon, unguided) = await (
      api.getOptics(),
      api.getImagingDefaults(),
      api.getAutofocusSettings(),
      api.getSiteSettings(),
      api.getFilterSet(),
      api.getCameraElectronics(),
      // #1137 — the §36 terrain skyline: the offline Tonight's Sky ranker
      // honours it, so it must come from the cache too, not only the server.
      // Best-effort: a daemon without the endpoint must not fail the whole
      // capture (null = not captured; the seed then leaves the notifier alone).
      _tryHorizon(api),
      // #1264 — the offline run builder cooks the probe / guard from it too.
      // Best-effort like the horizon: a failed read of this one section must
      // not fail the whole capture (null = not captured; the seed then keeps
      // the notifier's defaults).
      _tryUnguided(api),
    ).wait;
    await cache.saveSections(activeId, {
      'optics': ProfileApi.opticsToJson(optics),
      'imaging_defaults': ProfileApi.imagingDefaultsToJson(imaging),
      'autofocus': ProfileApi.autofocusSettingsToJson(autofocus),
      if (unguided != null)
        'unguided': ProfileApi.unguidedSettingsToJson(unguided),
      'site': ProfileApi.siteSettingsToJson(site),
      'filter_set': ProfileApi.filterSetToJson(filters),
      'camera_electronics': ProfileApi.cameraElectronicsToJson(electronics),
      if (horizon != null)
        'custom_horizon': ProfileApi.customHorizonToJson(horizon),
    });
  } catch (e) {
    debugPrint('[profile-cache] capture failed (offline cache stays stale): $e');
  }
}

Future<UnguidedSettings?> _tryUnguided(ProfileApi api) async {
  try {
    return await api.getUnguidedSettings();
  } catch (e) {
    debugPrint('[profile-cache] unguided settings not captured: $e');
    return null;
  }
}

Future<List<CustomHorizonPoint>?> _tryHorizon(ProfileApi api) async {
  try {
    return await api.getCustomHorizon();
  } catch (e) {
    debugPrint('[profile-cache] custom horizon not captured: $e');
    return null;
  }
}

/// Seed the planning settings notifiers from [profileId]'s cached sections so
/// an offline session plans with the user's real gear instead of constructor
/// defaults. Returns false when no snapshot exists for that profile (the
/// notifiers keep their defaults). Uses each notifier's hydrateGuarded seam —
/// the same one server hydration uses — so a later real hydrate wins races.
Future<bool> seedPlanningFromCache(
    ProviderContainer container, String profileId) async {
  final cache = await container.read(profileCacheServiceProvider).load();
  final sections = cache['sections'];
  final snap = sections is Map ? sections[profileId] : null;
  if (snap is! Map) return false;
  Map<String, dynamic> sec(String key) {
    final v = snap[key];
    return v is Map ? Map<String, dynamic>.from(v) : const {};
  }

  await container
      .read(opticsSettingsProvider.notifier)
      .hydrateGuarded(() async => ProfileApi.opticsFromJson(sec('optics')));
  await container.read(imagingDefaultsProvider.notifier).hydrateGuarded(
      () async => ProfileApi.imagingDefaultsFromJson(sec('imaging_defaults')));
  await container.read(autofocusSettingsProvider.notifier).hydrateGuarded(
      () async => ProfileApi.autofocusSettingsFromJson(sec('autofocus')));
  if (snap['unguided'] is Map) {
    await container.read(unguidedSettingsProvider.notifier).hydrateGuarded(
        () async => ProfileApi.unguidedSettingsFromJson(sec('unguided')));
  }
  await container
      .read(siteSettingsProvider.notifier)
      .hydrateGuarded(() async => ProfileApi.siteSettingsFromJson(sec('site')));
  await container.read(filterSetProvider.notifier).hydrateGuarded(
      () async => ProfileApi.filterSetFromJson(sec('filter_set')));
  await container.read(cameraElectronicsProvider.notifier).hydrateGuarded(
      () async => ProfileApi.cameraElectronicsFromJson(sec('camera_electronics')));
  // #1137 — a snapshot taken before the horizon was cached has no key: leave
  // the notifier alone rather than wiping a skyline the user may have staged.
  if (snap['custom_horizon'] is Map) {
    await container.read(customHorizonProvider.notifier).hydrateGuarded(
        () async => ProfileApi.customHorizonFromJson(sec('custom_horizon')));
  }
  return true;
}
