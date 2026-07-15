import 'package:flutter/foundation.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../models/profile_list.dart';
import '../services/profile_api.dart';
import '../services/profile_cache_service.dart';
import 'settings/autofocus_settings_state.dart';
import 'settings/camera_electronics_state.dart';
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
  const CachedProfile(
      {required this.id, required this.name, required this.hasSections});
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
    final (optics, imaging, autofocus, site, filters, electronics) = await (
      api.getOptics(),
      api.getImagingDefaults(),
      api.getAutofocusSettings(),
      api.getSiteSettings(),
      api.getFilterSet(),
      api.getCameraElectronics(),
    ).wait;
    await cache.saveSections(activeId, {
      'optics': ProfileApi.opticsToJson(optics),
      'imaging_defaults': ProfileApi.imagingDefaultsToJson(imaging),
      'autofocus': ProfileApi.autofocusSettingsToJson(autofocus),
      'site': ProfileApi.siteSettingsToJson(site),
      'filter_set': ProfileApi.filterSetToJson(filters),
      'camera_electronics': ProfileApi.cameraElectronicsToJson(electronics),
    });
  } catch (e) {
    debugPrint('[profile-cache] capture failed (offline cache stays stale): $e');
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
  await container
      .read(siteSettingsProvider.notifier)
      .hydrateGuarded(() async => ProfileApi.siteSettingsFromJson(sec('site')));
  await container.read(filterSetProvider.notifier).hydrateGuarded(
      () async => ProfileApi.filterSetFromJson(sec('filter_set')));
  await container.read(cameraElectronicsProvider.notifier).hydrateGuarded(
      () async => ProfileApi.cameraElectronicsFromJson(sec('camera_electronics')));
  return true;
}
