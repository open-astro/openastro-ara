import 'package:flutter/foundation.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../models/profile_list.dart';
import '../services/profile_api.dart';
import '../services/profile_cache_service.dart';
import 'settings/loaded_section.dart';
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
    // Parallel (like hydratePlanningSettings): this fires on every list load
    // AND after every mutation, so serializing the round-trips adds up.
    // Each section on its own (ADR 0015 rule 3): the strict readers refuse a
    // reply with a key missing or a value this client doesn't know, and one
    // refused (or failed) section keeps the copy the cache already holds
    // instead of costing the others their refresh. #1137 the §36 skyline and
    // #1264 the unguided knobs are captured the same way: a daemon without
    // the endpoint leaves them as cached.
    final reads = await Future.wait([
      _section('optics', () async => ProfileApi.opticsToJson(await api.getOptics())),
      _section('imaging_defaults',
          () async => ProfileApi.imagingDefaultsToJson(await api.getImagingDefaults())),
      _section('autofocus',
          () async => ProfileApi.autofocusSettingsToJson(await api.getAutofocusSettings())),
      _section('unguided',
          () async => ProfileApi.unguidedSettingsToJson(await api.getUnguidedSettings())),
      _section('site',
          () async => ProfileApi.siteSettingsToJson(await api.getSiteSettings())),
      _section('filter_set',
          () async => ProfileApi.filterSetToJson(await api.getFilterSet())),
      _section('camera_electronics',
          () async => ProfileApi.cameraElectronicsToJson(await api.getCameraElectronics())),
      _section('custom_horizon',
          () async => ProfileApi.customHorizonToJson(await api.getCustomHorizon())),
    ]);
    await cache.saveSections(
      activeId,
      {
        for (final r in reads)
          if (r.json != null) r.key: r.json,
      },
      keep: {
        for (final r in reads)
          if (r.json == null) r.key,
      },
    );
  } catch (e) {
    debugPrint('[profile-cache] capture failed (offline cache stays stale): $e');
  }
}

/// One section's wire JSON for the cache, or null when the read failed (the
/// cache then keeps the copy it holds).
Future<({String key, Map<String, dynamic>? json})> _section(
  String key,
  Future<Map<String, dynamic>> Function() read,
) async {
  try {
    return (key: key, json: await read());
  } catch (e) {
    debugPrint('[profile-cache] $key not captured (the cached copy stays): $e');
    return (key: key, json: null);
  }
}

/// Seed the planning settings notifiers from [profileId]'s cached sections so
/// an offline session plans with the user's real gear. Returns false when no
/// snapshot exists for that profile (the notifiers keep what they hold). Each
/// section is seeded as a cache copy (ADR 0015): readable for planning, never
/// saved, and replaced by the rig's copy once it loads. Each is read with the
/// same strict reader as the rig's copy (rule 3): a snapshot with no key for
/// a section, or one from before a field existed, leaves that section not
/// loaded, never a client default that planning would take for the rig's.
Future<bool> seedPlanningFromCache(
    ProviderContainer container, String profileId) async {
  final cache = await container.read(profileCacheServiceProvider).load();
  final sections = cache['sections'];
  final snap = sections is Map ? sections[profileId] : null;
  if (snap is! Map) return false;
  void seed<T>(
    String key,
    SectionNotifier<T> section,
    T Function(Map<String, dynamic>) read,
  ) {
    final v = snap[key];
    if (v is! Map) return;
    try {
      section.seed(read(Map<String, dynamic>.from(v)));
    } catch (e) {
      debugPrint('[profile-cache] cached $key not readable (stays not loaded): $e');
    }
  }

  seed('optics', container.read(opticsSettingsProvider.notifier),
      ProfileApi.opticsFromJson);
  seed('imaging_defaults', container.read(imagingDefaultsProvider.notifier),
      ProfileApi.imagingDefaultsFromJson);
  seed('autofocus', container.read(autofocusSettingsProvider.notifier),
      ProfileApi.autofocusSettingsFromJson);
  seed('unguided', container.read(unguidedSettingsProvider.notifier),
      ProfileApi.unguidedSettingsFromJson);
  seed('site', container.read(siteSettingsProvider.notifier),
      ProfileApi.siteSettingsFromJson);
  seed('filter_set', container.read(filterSetProvider.notifier),
      ProfileApi.filterSetFromJson);
  seed('camera_electronics', container.read(cameraElectronicsProvider.notifier),
      ProfileApi.cameraElectronicsFromJson);
  // #1137 — a snapshot taken before the horizon was cached has no key: the
  // skyline stays as it is.
  seed('custom_horizon', container.read(customHorizonProvider.notifier),
      ProfileApi.customHorizonFromJson);
  return true;
}
