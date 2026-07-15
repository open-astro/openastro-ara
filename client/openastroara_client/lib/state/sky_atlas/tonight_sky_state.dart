import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../services/tonight_sky_api.dart';
import '../../util/tonight_sky_local.dart';
import '../saved_server_state.dart';
import '../settings/camera_electronics_state.dart';
import '../settings/filter_set_state.dart';
import '../settings/optics_settings_state.dart';
import '../settings/site_settings_state.dart';

/// §36/§25.5 — Tonight's Sky ranking (curated objects above the site horizon
/// now, highest first). Connected: the daemon's full ranking (OpenNGC catalog,
/// filter advice, custom horizon). No server (§2 offline planning): a local
/// ranking over the daemon's starter catalog against the CACHED site + optics
/// (seeded by the offline launch flow) — planning during the day with the rig
/// off still shows what's worth shooting tonight. Auto-disposed so the list
/// re-ranks for the current time each time the view is opened;
/// `ref.invalidate(tonightSkyProvider)` forces a manual refresh.
final tonightSkyProvider = FutureProvider.autoDispose<List<TonightSkyObject>>((
  ref,
) async {
  // The awaitable variant rather than collapsing a still-loading state to
  // null: that would resolve this provider to data([]) before the servers
  // finished loading and flash the "connect a server" empty state for a user
  // who does have one saved. Awaiting keeps Tonight's Sky in `loading` until
  // the list is known.
  final server = await ref.watch(activeServerFutureProvider.future);
  if (server == null) {
    final site = ref.watch(siteSettingsProvider);
    // An unset site (the constructor's 0,0) would silently rank for a spot in
    // the Gulf of Guinea — return empty instead; the panel's empty state
    // explains how to get a site offline (cached profile / connect once).
    if (site.latitudeDeg == 0 && site.longitudeDeg == 0) {
      return const <TonightSkyObject>[];
    }
    return computeTonightSkyLocal(
      site: site,
      optics: ref.watch(opticsSettingsProvider),
      filterSet: ref.watch(filterSetProvider),
      electronics: ref.watch(cameraElectronicsProvider),
      atUtc: DateTime.now().toUtc(),
    );
  }
  final api = TonightSkyApi(server);
  // Force-close on dispose so navigating away from the panel cancels an in-flight
  // fetch instead of leaving the socket open until the receive timeout.
  ref.onDispose(api.close);
  // Watching (not reading) the overrides means an Apply/Reset in the overrides
  // dialog refetches the ranking automatically — no manual invalidate needed.
  final overrides = ref.watch(tonightSkyOverridesProvider);
  return api.fetch(overrides: overrides.isActive ? overrides : null);
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
