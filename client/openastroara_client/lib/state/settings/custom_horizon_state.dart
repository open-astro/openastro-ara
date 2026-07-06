import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../services/profile_api.dart';

/// §36 custom terrain horizon — one skyline vertex: the sky altitude at a
/// compass azimuth (0° = north, clockwise). Mirrors the daemon's
/// `CustomHorizonPointDto`; the daemon canonicalizes on PUT (sort, dedupe,
/// 360→0 wrap) and echoes the canonical list back.
class CustomHorizonPoint {
  final double azimuthDeg;
  final double altitudeDeg;

  const CustomHorizonPoint({
    required this.azimuthDeg,
    required this.altitudeDeg,
  });

  CustomHorizonPoint copyWith({double? azimuthDeg, double? altitudeDeg}) =>
      CustomHorizonPoint(
        azimuthDeg: azimuthDeg ?? this.azimuthDeg,
        altitudeDeg: altitudeDeg ?? this.altitudeDeg,
      );
}

/// The active profile's skyline vertices, ordered by azimuth for display.
/// Empty = none entered — the daemon then falls back to the flat default
/// altitude even with "use custom horizon" on (and Tonight's Sky /the chart
/// overlay say so via CustomHorizonIgnored).
class CustomHorizonNotifier extends Notifier<List<CustomHorizonPoint>> {
  @override
  List<CustomHorizonPoint> build() => const [];

  /// Stage a new vertex locally (persisted on the panel's Save). Appended,
  /// not inserted sorted: row indices must stay STABLE while the user edits —
  /// the panel's editable rows bind to their index, so reordering mid-edit
  /// would rebind a just-edited field to a different vertex (review r1).
  /// Sorting happens where indices are allowed to change wholesale: hydrate
  /// and the daemon's canonical echo on Save.
  void addPoint(double azimuthDeg, double altitudeDeg) {
    if (!_validAzimuth(azimuthDeg) || !_validAltitude(altitudeDeg)) return;
    state = [
      ...state,
      CustomHorizonPoint(azimuthDeg: azimuthDeg, altitudeDeg: altitudeDeg),
    ];
  }

  void removeAt(int index) {
    if (index < 0 || index >= state.length) return;
    state = [...state]..removeAt(index);
  }

  /// Edit a vertex IN PLACE — deliberately no re-sort (see [addPoint]).
  /// Out-of-range values are rejected like the sibling site setters, so the
  /// daemon's 422 range check is unreachable from this UI.
  void updateAt(int index, {double? azimuthDeg, double? altitudeDeg}) {
    if (index < 0 || index >= state.length) return;
    if (azimuthDeg != null && !_validAzimuth(azimuthDeg)) return;
    if (altitudeDeg != null && !_validAltitude(altitudeDeg)) return;
    final next = [...state];
    next[index] = next[index].copyWith(
      azimuthDeg: azimuthDeg,
      altitudeDeg: altitudeDeg,
    );
    state = next;
  }

  Future<void> hydrateFromServer(ProfileApi api) async {
    state = _sorted(await api.getCustomHorizon());
  }

  /// PUT the staged skyline; the daemon canonicalizes and echoes it back,
  /// which becomes the new state (so client and daemon agree byte-for-byte).
  Future<void> persistToServer(ProfileApi api) async {
    state = _sorted(await api.putCustomHorizon(state));
  }

  // The daemon's CustomHorizonValidator bounds, mirrored client-side.
  static bool _validAzimuth(double v) => !v.isNaN && v >= 0 && v <= 360;
  static bool _validAltitude(double v) => !v.isNaN && v >= -10 && v <= 90;

  static List<CustomHorizonPoint> _sorted(List<CustomHorizonPoint> points) =>
      [...points]..sort((a, b) => a.azimuthDeg.compareTo(b.azimuthDeg));
}

final customHorizonProvider =
    NotifierProvider<CustomHorizonNotifier, List<CustomHorizonPoint>>(
      CustomHorizonNotifier.new,
    );
