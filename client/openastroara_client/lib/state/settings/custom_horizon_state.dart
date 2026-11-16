import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../services/profile_api.dart';
import 'loaded_section.dart';

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
/// overlay say so via CustomHorizonIgnored). ADR 0015: nothing until the
/// rig (or the offline cache) has answered, never an empty skyline.
class CustomHorizonNotifier extends SectionNotifier<List<CustomHorizonPoint>> {
  /// Stage a new vertex locally (persisted on the panel's Save). Appended,
  /// not inserted sorted: row indices must stay STABLE while the user edits —
  /// the panel's editable rows bind to their index, so reordering mid-edit
  /// would rebind a just-edited field to a different vertex (review r1).
  /// Sorting happens where indices are allowed to change wholesale: hydrate
  /// and the daemon's canonical echo on Save.
  void addPoint(double azimuthDeg, double altitudeDeg) {
    if (!_validAzimuth(azimuthDeg) || !_validAltitude(altitudeDeg)) return;
    edit(
      (c) => [
        ...c,
        CustomHorizonPoint(azimuthDeg: azimuthDeg, altitudeDeg: altitudeDeg),
      ],
    );
  }

  void removeAt(int index) => edit(
    (c) => index < 0 || index >= c.length ? c : ([...c]..removeAt(index)),
  );

  /// Edit a vertex IN PLACE — deliberately no re-sort (see [addPoint]).
  /// Out-of-range values are rejected like the sibling site setters, so the
  /// daemon's 422 range check is unreachable from this UI.
  void updateAt(int index, {double? azimuthDeg, double? altitudeDeg}) {
    if (azimuthDeg != null && !_validAzimuth(azimuthDeg)) return;
    if (altitudeDeg != null && !_validAltitude(altitudeDeg)) return;
    edit((c) {
      if (index < 0 || index >= c.length) return c;
      final next = [...c];
      next[index] = next[index].copyWith(
        azimuthDeg: azimuthDeg,
        altitudeDeg: altitudeDeg,
      );
      return next;
    });
  }

  /// The daemon canonicalizes (sort/dedupe/wrap, 422 on out-of-range values)
  /// and echoes the canonical list, which becomes the new value.
  @override
  Future<Tagged<List<CustomHorizonPoint>>> fetch(ProfileApi api) async {
    final r = await api.getCustomHorizonSection();
    return (value: _sorted(r.value), etag: r.etag);
  }

  @override
  Future<Tagged<List<CustomHorizonPoint>>> send(
    ProfileApi api,
    List<CustomHorizonPoint> value,
    String? etag,
  ) async {
    final r = await api.putCustomHorizonSection(value, ifMatch: etag);
    return (value: _sorted(r.value), etag: r.etag);
  }

  // The daemon's CustomHorizonValidator bounds, mirrored client-side.
  static bool _validAzimuth(double v) => !v.isNaN && v >= 0 && v <= 360;
  static bool _validAltitude(double v) => !v.isNaN && v >= -10 && v <= 90;

  static List<CustomHorizonPoint> _sorted(List<CustomHorizonPoint> points) =>
      [...points]..sort((a, b) => a.azimuthDeg.compareTo(b.azimuthDeg));
}

final customHorizonProvider =
    NotifierProvider<
      CustomHorizonNotifier,
      LoadedSection<List<CustomHorizonPoint>>
    >(CustomHorizonNotifier.new);
