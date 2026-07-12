import 'package:flutter_riverpod/flutter_riverpod.dart';

import 'settings_sync_mixin.dart';

import '../../services/profile_api.dart';

/// NEXTGEN §1/§4 planning filter kind. Wire values are the daemon's all-
/// lowercase enum tokens (`l`/`r`/`g`/`b`/`osc`/`ha`/`oiii`/`sii`/`duo`/`tri`).
enum FilterKind {
  l('l', 'L (luminance)'),
  r('r', 'R'),
  g('g', 'G'),
  b('b', 'B'),
  osc('osc', 'OSC / DSLR (no filter)'),
  ha('ha', 'Hα'),
  oiii('oiii', 'OIII'),
  sii('sii', 'SII'),
  duo('duo', 'Dual-band (L-eXtreme …)'),
  tri('tri', 'Tri-band (L-eNhance …)');

  const FilterKind(this.wire, this.label);

  /// The daemon's wire token.
  final String wire;

  /// Human-readable dropdown label.
  final String label;

  static FilterKind fromWire(String? v) => FilterKind.values.firstWhere(
        (k) => k.wire == v,
        orElse: () => FilterKind.l,
      );

  /// The kind's default effective passband in nm — mirrors the daemon's
  /// `OptimalSubCalculator.DefaultBandwidthNm` table so the filter-set editor
  /// can show what a 0 ("use default") bandwidth means. Duo/tri use the
  /// per-pixel single-line width (each OSC Bayer channel sees only the line
  /// that lands in it).
  double get defaultBandwidthNm => switch (this) {
        FilterKind.l || FilterKind.osc => 100,
        FilterKind.r || FilterKind.g || FilterKind.b => 80,
        FilterKind.ha ||
        FilterKind.oiii ||
        FilterKind.sii ||
        FilterKind.duo =>
          7,
        FilterKind.tri => 8,
      };
}

/// One filter in the planning filter set. [name] is the key sequences match
/// on (case-insensitive, so it should mirror the filter-wheel label);
/// [bandwidthNm] 0 = use the kind's default effective passband.
class PlanningFilter {
  final String name;
  final FilterKind kind;
  final double bandwidthNm;

  const PlanningFilter({
    required this.name,
    required this.kind,
    this.bandwidthNm = 0,
  });

  PlanningFilter copyWith({String? name, FilterKind? kind, double? bandwidthNm}) =>
      PlanningFilter(
        name: name ?? this.name,
        kind: kind ?? this.kind,
        bandwidthNm: bandwidthNm ?? this.bandwidthNm,
      );
}

/// NEXTGEN §1/§4 — the user's declared planning filter set, captured once at
/// setup (planning runs offline, so this is a profile setting, not a live
/// filter-wheel read). Deliberately separate from the equipment FilterInfo,
/// which must round-trip NINA imports untouched. Backed by the daemon's
/// `GET`/`PUT /api/v1/profile/filter-set`.
class FilterSetSettings {
  final List<PlanningFilter> filters;

  const FilterSetSettings({this.filters = const []});
}

class FilterSetNotifier extends Notifier<FilterSetSettings>
    with SettingsSyncMixin<FilterSetSettings> {
  @override
  FilterSetSettings build() => const FilterSetSettings();

  /// Add a filter. No-ops on an empty or duplicate (case-insensitive) name —
  /// mirroring the daemon's PUT validation so a rejected save can't desync.
  void addFilter(PlanningFilter filter) {
    final name = filter.name.trim();
    if (name.isEmpty || _hasName(name)) return;
    state = FilterSetSettings(
        filters: [...state.filters, filter.copyWith(name: name)]);
  }

  void removeAt(int index) {
    if (index < 0 || index >= state.filters.length) return;
    final next = [...state.filters]..removeAt(index);
    state = FilterSetSettings(filters: next);
  }

  /// Replace the filter at [index]. No-ops when the new name is empty or
  /// collides with a DIFFERENT entry's name.
  void updateAt(int index, PlanningFilter filter) {
    if (index < 0 || index >= state.filters.length) return;
    final name = filter.name.trim();
    if (name.isEmpty) return;
    for (var i = 0; i < state.filters.length; i++) {
      if (i != index && state.filters[i].name.toLowerCase() == name.toLowerCase()) {
        return;
      }
    }
    final next = [...state.filters];
    next[index] = filter.copyWith(name: name);
    state = FilterSetSettings(filters: next);
  }

  /// Seed entries from the connected filter wheel's slot labels — a
  /// convenience so the user only picks kinds instead of retyping names.
  /// Existing names are kept; only new labels are appended (kind defaults to
  /// a guess from the label, broadband L when unrecognisable).
  void seedFromWheelLabels(Iterable<String> labels) {
    final added = <PlanningFilter>[];
    for (final raw in labels) {
      final name = raw.trim();
      if (name.isEmpty || _hasName(name)) continue;
      if (added.any((f) => f.name.toLowerCase() == name.toLowerCase())) continue;
      added.add(PlanningFilter(name: name, kind: guessKind(name)));
    }
    if (added.isEmpty) return;
    state = FilterSetSettings(filters: [...state.filters, ...added]);
  }

  bool _hasName(String name) => state.filters
      .any((f) => f.name.toLowerCase() == name.trim().toLowerCase());

  /// Best-effort kind guess from a wheel label ('Ha 3nm' → ha, 'OIII' → oiii,
  /// 'Red' → r …). Exposed for the seed flow + tests; the user can always
  /// correct the dropdown.
  static FilterKind guessKind(String label) {
    final l = label.toLowerCase();
    // Multi-band product names first — they embed line-filter substrings
    // ('l-enhance' contains "ha", 'l-extreme' contains "tre"), so a naive
    // contains('ha') check would misfile them as Hα.
    if (l.contains('extreme') || l.contains('duo') || l.contains('dual')) {
      return FilterKind.duo;
    }
    if (l.contains('enhance') || l.contains('tri')) return FilterKind.tri;
    // Line filters match whole label tokens (split on non-alphanumerics) so
    // substrings inside ordinary words can't trigger them.
    final tokens = l
        .split(RegExp('[^a-z0-9α]+'))
        .where((t) => t.isNotEmpty)
        .toSet();
    if (tokens.contains('ha') || tokens.contains('hα') || tokens.contains('halpha')) {
      return FilterKind.ha;
    }
    if (tokens.contains('oiii') || tokens.contains('o3')) return FilterKind.oiii;
    if (tokens.contains('sii') || tokens.contains('s2')) return FilterKind.sii;
    if (tokens.contains('r') || l.startsWith('red')) return FilterKind.r;
    if (tokens.contains('g') || l.startsWith('green')) return FilterKind.g;
    if (tokens.contains('b') || l.startsWith('blue')) return FilterKind.b;
    if (l.contains('osc') || l.contains('color') || l.contains('colour')) {
      return FilterKind.osc;
    }
    return FilterKind.l;
  }

  /// Replace local state with what the daemon currently holds.
  Future<void> hydrateFromServer(ProfileApi api) =>
      hydrateGuarded(() => api.getFilterSet());

  /// Send the current local state to the daemon; returns its echo.
  Future<FilterSetSettings> persistToServer(ProfileApi api) =>
      persistGuarded((sent) => api.putFilterSet(sent));
}

final filterSetProvider =
    NotifierProvider<FilterSetNotifier, FilterSetSettings>(
        FilterSetNotifier.new);
