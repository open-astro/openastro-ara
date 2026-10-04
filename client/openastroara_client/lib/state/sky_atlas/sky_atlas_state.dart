import 'package:flutter_riverpod/flutter_riverpod.dart';

/// State for the Planning tab's planetarium (§25.5.4 + §36): the
/// Explore/Tonight's-Sky mode toggle, the selected sky target, and the
/// one-shot command bus `StellariumView` forwards to the self-driven
/// planetarium page.

/// The planetarium alone, or with one docked panel beside it: Tonight's Sky,
/// or the by-hand Rotate camera readout against the framing on the sky.
enum SkyAtlasMode { catalogView, tonightsSky, rotateCamera }

class SkyAtlasModeNotifier extends Notifier<SkyAtlasMode> {
  @override
  SkyAtlasMode build() => SkyAtlasMode.catalogView;
  void set(SkyAtlasMode m) => state = m;

  /// Flip between the full-bleed planetarium and the docked Tonight's Sky panel
  /// (from the rotate panel, Tonight's Sky takes its place — one dock at a time).
  void toggle() => _toggle(SkyAtlasMode.tonightsSky);

  /// Flip the docked Rotate camera panel the same way.
  void toggleRotateCamera() => _toggle(SkyAtlasMode.rotateCamera);

  void _toggle(SkyAtlasMode panel) =>
      state = state == panel ? SkyAtlasMode.catalogView : panel;
}

final skyAtlasModeProvider =
    NotifierProvider<SkyAtlasModeNotifier, SkyAtlasMode>(
        SkyAtlasModeNotifier.new);

/// §36 — a chosen sky target (equatorial J2000 coordinates + a display name).
/// The planetarium (`StellariumView`) flies to it; a connected mount can GoTo it.
class SkyTarget {
  final double raDeg;
  final double decDeg;
  final String name;
  const SkyTarget({required this.raDeg, required this.decDeg, required this.name});
}

/// The currently-selected planetarium target, or null when nothing is selected.
/// Tonight's Sky (and, later, the search bar) set this; `StellariumView` listens
/// and centres the view. Like the search notifier it always notifies, so
/// re-selecting the same object re-centres — consume it with `ref.listen`, not
/// `ref.watch`.
class SkyTargetNotifier extends Notifier<SkyTarget?> {
  @override
  SkyTarget? build() => null;
  void set(SkyTarget target) => state = target;
  void clear() => state = null;

  @override
  bool updateShouldNotify(SkyTarget? previous, SkyTarget? next) => true;
}

final skyTargetProvider =
    NotifierProvider<SkyTargetNotifier, SkyTarget?>(SkyTargetNotifier.new);

/// §36 slice 3b — a one-shot command for the self-driven planetarium page.
/// Flutter chrome (e.g. the Tonight's Sky panel's recentre button) writes a
/// command map here; [StellariumView] `ref.listen`s it and forwards the value
/// over the loopback `StellariumServer` to the page's `/aracmd` handler. The
/// native webview has no Dart→page JS bridge, so panel→page actions must ride
/// this loopback seam — writing `skyTargetProvider` does NOT move the view (the
/// planetarium never reads it). The recentre action sends
/// `{'type':'goto','ra':<deg>,'dec':<deg>}` (the page centres directly on the
/// coordinates, no name lookup). Always notifies so re-issuing the same goto
/// re-centres — consume it with `ref.listen` (a side-effect), never `ref.watch`.
class PlanetariumCommandNotifier extends Notifier<Map<String, Object?>?> {
  @override
  Map<String, Object?>? build() => null;
  void send(Map<String, Object?> cmd) => state = cmd;

  /// Reset to null once the command has been forwarded to the page — this is a
  /// fire-and-forget bus, so a consumed command shouldn't linger in state where a
  /// future reader could mistake it for a fresh one.
  void clear() => state = null;

  // Notify on every non-null command (so two identical consecutive sends each
  // re-fire — a re-tapped recentre must re-issue the goto), but stay silent when
  // `next` is null. That keeps `clear()` from waking the listener with a consumed
  // command — the listener never sees null, so it needs no null guard.
  @override
  bool updateShouldNotify(
          Map<String, Object?>? previous, Map<String, Object?>? next) =>
      next != null;
}

final planetariumCommandProvider =
    NotifierProvider<PlanetariumCommandNotifier, Map<String, Object?>?>(
        PlanetariumCommandNotifier.new);
