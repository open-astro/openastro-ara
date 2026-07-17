import 'dart:isolate';

import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../services/profile_api.dart';
import '../../services/tonight_sky_api.dart';
import '../../util/tonight_sky_local.dart';
import '../saved_server_state.dart';
import '../settings/camera_electronics_state.dart';
import '../settings/custom_horizon_state.dart';
import '../settings/filter_set_state.dart';
import '../settings/optics_settings_state.dart';
import '../settings/site_settings_state.dart';
import 'dso_catalog_state.dart';

/// §36/§25.5 — Tonight's Sky ranking (curated objects above the site horizon
/// tonight, highest first), computed CLIENT-SIDE unconditionally (PORT_DECISIONS
/// 2026-07-15) from the live-or-offline-seeded settings notifiers + the
/// mirrored openngc-dso catalog — connected sessions and daytime-offline
/// planning share one path. Auto-disposed so the list re-ranks for the current
/// time each time the view is opened; `ref.invalidate(tonightSkyProvider)`
/// forces a manual refresh.
/// Hydrates the profile sections Tonight's Sky ranks from (site, optics,
/// filters, camera electronics) ONCE per active server. Without this, those
/// notifiers only load when their Options panels mount — so a fresh app launch
/// ranked against the constructor defaults (site 0,0 → the "no site" empty
/// state) until the user happened to visit Settings. Deliberately NOT
/// auto-disposed and watched (not re-run) by the ranker: hydration mutates the
/// very notifiers the ranker watches, so folding it into the ranker would loop.
/// Best-effort — offline, the notifiers keep their cached/seeded state.
final planningSettingsBootstrapProvider = FutureProvider<void>((ref) async {
  final server = await ref.watch(activeServerFutureProvider.future);
  if (server == null) return;
  final api = ProfileApi(server);
  try {
    await Future.wait([
      ref.read(siteSettingsProvider.notifier).hydrateFromServer(api),
      ref.read(opticsSettingsProvider.notifier).hydrateFromServer(api),
      ref.read(filterSetProvider.notifier).hydrateFromServer(api),
      ref.read(cameraElectronicsProvider.notifier).hydrateFromServer(api),
      // §36 custom terrain skyline — the ranker gates visibility on it when
      // the site has useCustomHorizon on.
      ref.read(customHorizonProvider.notifier).hydrateFromServer(api),
    ]);
  } catch (_) {
    // best-effort: an unreachable daemon leaves the cached/offline state
  }
});

final tonightSkyProvider = FutureProvider.autoDispose<List<TonightSkyObject>>((
  ref,
) async {
  // The awaitable variant rather than collapsing a still-loading state to
  // null: that would resolve this provider to data([]) before the servers
  // finished loading and flash the "no site" empty state for a user whose
  // cached site is about to load. Await keeps Tonight's Sky in `loading`.
  await ref.watch(activeServerFutureProvider.future);
  // Ensure the settings this ranker reads are actually loaded from the daemon
  // (fresh launch: nothing else hydrates them until an Options panel mounts).
  await ref.watch(planningSettingsBootstrapProvider.future);
  final site = ref.watch(siteSettingsProvider);
  // An unset site (the constructor's 0,0) would silently rank for a spot in
  // the Gulf of Guinea — return empty instead; the panel's empty state
  // explains how to get a site (Settings → Site, or a cached profile).
  if (site.latitudeDeg == 0 && site.longitudeDeg == 0) {
    return const <TonightSkyObject>[];
  }
  var optics = ref.watch(opticsSettingsProvider);
  // §36.8 slice 4b what-if overrides: swap the optical train fields the user
  // dialed in; mosaic tiles enlarge the framing FOV per axis.
  final overrides = ref.watch(tonightSkyOverridesProvider);
  var mosaicX = 1;
  var mosaicY = 1;
  if (overrides.isActive) {
    optics = OpticsSettings(
      focalLengthMm: overrides.focalLengthMm ?? optics.focalLengthMm,
      reducerFactor: overrides.reducer ?? optics.reducerFactor,
      sensorWidthPx: overrides.sensorW ?? optics.sensorWidthPx,
      sensorHeightPx: overrides.sensorH ?? optics.sensorHeightPx,
      pixelSizeUm: overrides.pixelUm ?? optics.pixelSizeUm,
      apertureMm: optics.apertureMm,
    );
    mosaicX = overrides.mosaicX;
    mosaicY = overrides.mosaicY;
  }
  final filterSet = ref.watch(filterSetProvider);
  final electronics = ref.watch(cameraElectronicsProvider);
  // The mirrored openngc-dso catalog when this machine has one; the
  // ranker falls back to the 20-object starter list otherwise.
  final catalog = await ref.watch(dsoCatalogProvider.future);
  // Plain records so the isolate payload stays model-free.
  final horizonPoints = ref
      .watch(customHorizonProvider)
      .map((p) => (p.azimuthDeg, p.altitudeDeg))
      .toList();
  final at = DateTime.now().toUtc();
  final opticsFinal = optics;
  // Rank OFF the UI isolate: the mirrored catalog is thousands of rows × a
  // 289-sample window scan each — synchronous on the UI thread is jank.
  return Isolate.run(() => computeTonightSkyLocal(
        site: site,
        optics: opticsFinal,
        filterSet: filterSet,
        electronics: electronics,
        catalog: catalog,
        customHorizon: horizonPoints,
        mosaicTilesX: mosaicX,
        mosaicTilesY: mosaicY,
        atUtc: at,
      ));
});

/// §36.8 slice 4b — the session's Tonight's Sky what-if overrides (optical
/// train fields + mosaic tiles). NOT auto-disposed on purpose: a user who has
/// dialed in "what could I shoot with the 0.7× reducer?" keeps that lens while
/// hopping between tabs; it resets with the app (overrides are a what-if, not
/// a setting — a persistent change belongs in Settings → Optics).
class TonightOverridesNotifier extends Notifier<TonightOverrides> {
  @override
  TonightOverrides build() => TonightOverrides.none;

  void set(TonightOverrides value) => state = value;

  void clear() => state = TonightOverrides.none;
}

final tonightSkyOverridesProvider =
    NotifierProvider<TonightOverridesNotifier, TonightOverrides>(
      TonightOverridesNotifier.new,
    );

/// The Tonight's Sky row the user last tapped (by [TonightSkyObject.id]), or
/// null when nothing is selected. Tapping a row frames that object on the
/// planetarium; the highlight tells the user which row drove the atlas. Auto-
/// disposed so the selection clears when the panel unmounts — a highlight that
/// silently survived into the next open would claim a framing that is no
/// longer on screen.
class SelectedTonightObjectNotifier extends Notifier<String?> {
  @override
  String? build() => null;

  void select(String id) => state = id;
}

final selectedTonightObjectProvider =
    NotifierProvider.autoDispose<SelectedTonightObjectNotifier, String?>(
      SelectedTonightObjectNotifier.new,
    );
