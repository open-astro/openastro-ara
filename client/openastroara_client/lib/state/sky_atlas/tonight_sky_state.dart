import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../services/tonight_sky_api.dart';
import '../saved_server_state.dart';

/// §36/§25.5 — the active server's Tonight's Sky ranking (curated objects above
/// the site horizon now, highest first). Empty when no server is connected.
/// Auto-disposed so the list refetches (re-ranks for the current time) each time
/// the Tonight's Sky view is opened; `ref.invalidate(tonightSkyProvider)` forces
/// a manual refresh.
final tonightSkyProvider = FutureProvider.autoDispose<List<TonightSkyObject>>((
  ref,
) async {
  // The awaitable variant rather than collapsing a still-loading state to
  // null: that would resolve this provider to data([]) before the servers
  // finished loading and flash the "connect a server" empty state for a user
  // who does have one saved. Awaiting keeps Tonight's Sky in `loading` until
  // the list is known.
  final server = await ref.watch(activeServerFutureProvider.future);
  if (server == null) return const <TonightSkyObject>[];
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
