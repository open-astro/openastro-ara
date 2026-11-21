import 'dart:async';
import 'dart:convert';
import 'dart:io';

import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:path_provider/path_provider.dart';

import '../../services/dso_catalog_service.dart' show designationKey;
import '../../services/tonight_sky_api.dart';
import '../../widgets/sky_atlas/tonight_sky_panel.dart'
    show TonightWindowState, windowStateFor;
import '../../models/stats/stats_target.dart';
import '../simple_run_state.dart';
import '../stats/stats_targets_state.dart';
import 'tonight_sky_state.dart';

/// Tonight's Best screen (#1900): the ranker's list as the person sorts and
/// filters it. The screen renders this module and holds no sort, filter or
/// marker logic.

enum BestSort { score, transit, altitudeNow }

/// Show: the whole ranked night, or the Targets whose dark window is open now.
enum BestShow { allNight, upNow }

enum BestType { galaxy, nebula, cluster, planetary, other }

/// Peak altitude choices (degrees); 0 is Any.
const kBestPeakAlts = [0, 30, 45, 60];

/// The Type chip of an OpenNGC type code. Own switch, not `typeClass`: that
/// one keeps dark nebulae apart and passes an unknown code through.
BestType bestTypeOf(String openNgcCode) => switch (openNgcCode) {
  'G' || 'GGroup' || 'GPair' || 'GTrpl' => BestType.galaxy,
  'HII' ||
  'EmN' ||
  'RfN' ||
  'Neb' ||
  'SNR' ||
  'Cl+N' ||
  'DrkN' => BestType.nebula,
  'OCl' || 'GCl' || '*Ass' => BestType.cluster,
  'PN' => BestType.planetary,
  _ => BestType.other,
};

/// The kept choices (story 20), saved on this device.
class BestChoices {
  const BestChoices({
    this.sort = BestSort.score,
    this.types = const {},
    this.show = BestShow.allNight,
    this.minPeakAltDeg = 0,
    this.fitsField = false,
    this.windowFromMin,
    this.windowToMin,
  });

  final BestSort sort;

  /// Empty = All.
  final Set<BestType> types;
  final BestShow show;
  final int minPeakAltDeg;
  final bool fitsField;

  /// Imaging hours as minutes of the local day (0-1439); both null = no
  /// range. A To at or before From runs past midnight (21:00-01:00).
  final int? windowFromMin;
  final int? windowToMin;

  BestChoices copyWith({
    BestSort? sort,
    Set<BestType>? types,
    BestShow? show,
    int? minPeakAltDeg,
    bool? fitsField,
    int? windowFromMin,
    int? windowToMin,
    bool clearWindow = false,
  }) => BestChoices(
    sort: sort ?? this.sort,
    types: types ?? this.types,
    show: show ?? this.show,
    minPeakAltDeg: minPeakAltDeg ?? this.minPeakAltDeg,
    fitsField: fitsField ?? this.fitsField,
    windowFromMin: clearWindow ? null : windowFromMin ?? this.windowFromMin,
    windowToMin: clearWindow ? null : windowToMin ?? this.windowToMin,
  );

  /// Whether a filter (not Sort) is set.
  bool get filtered =>
      types.isNotEmpty ||
      show != BestShow.allNight ||
      minPeakAltDeg != 0 ||
      fitsField ||
      windowFromMin != null;

  Map<String, Object> toJson() => {
    'sort': sort.name,
    'types': [for (final t in types) t.name],
    'show': show.name,
    'minPeakAltDeg': minPeakAltDeg,
    'fitsField': fitsField,
    'windowFromMin': ?windowFromMin,
    'windowToMin': ?windowToMin,
  };

  /// Per field: an unknown or wrongly typed value falls back to its default,
  /// and never takes the other fields with it.
  factory BestChoices.fromJson(Object? raw) {
    if (raw is! Map) return const BestChoices();
    final types = raw['types'];
    final peak = raw['minPeakAltDeg'];
    final from = raw['windowFromMin'];
    final to = raw['windowToMin'];
    // Only a whole range counts: one end alone, or a minute outside the day,
    // drops both.
    final range =
        from is int && to is int && _minuteOfDay(from) && _minuteOfDay(to);
    return BestChoices(
      sort: BestSort.values.asNameMap()[raw['sort']] ?? BestSort.score,
      types: {
        if (types is List)
          for (final t in types) ?BestType.values.asNameMap()[t],
      },
      show: BestShow.values.asNameMap()[raw['show']] ?? BestShow.allNight,
      // A hand-edited 30.0 equals 30 but is a double, not an int.
      minPeakAltDeg: kBestPeakAlts.contains(peak) ? (peak as num).toInt() : 0,
      fitsField: raw['fitsField'] is bool ? raw['fitsField'] as bool : false,
      windowFromMin: range ? from : null,
      windowToMin: range ? to : null,
    );
  }
}

bool _minuteOfDay(int m) => m >= 0 && m < 24 * 60;

/// Whether the dark window [start]..[end] overlaps the daily range
/// [fromMin]..[toMin] (minutes of the local day) on any day the window
/// touches. A range whose To is at or before From ends on the next day, so
/// equal ends cover the whole day. Touching ends overlap, as the window's own
/// open test (`windowStateFor`) includes its ends. No window: no overlap.
bool windowOverlapsRange(
  DateTime? start,
  DateTime? end,
  int fromMin,
  int toMin,
) {
  if (start == null || end == null) return false;
  final s = start.toLocal();
  final e = end.toLocal();
  final spanDays = toMin <= fromMin ? 1 : 0;
  // The range that began the day before the window may still run into it.
  for (
    var d = -1;
    d <= 0 || DateTime(s.year, s.month, s.day + d).isBefore(e);
    d++
  ) {
    final day = DateTime(s.year, s.month, s.day + d);
    final from = DateTime(day.year, day.month, day.day, 0, fromMin);
    final to = DateTime(day.year, day.month, day.day + spanDays, 0, toMin);
    if (!from.isAfter(e) && !to.isBefore(s)) return true;
  }
  return false;
}

/// One listed Target with what the screen shows beside the ranker's row.
class BestRow {
  const BestRow({
    required this.object,
    required this.type,
    required this.upNow,
    required this.belowHorizon,
    required this.inQueue,
    required this.collected,
    required this.moonSeparationDeg,
  });

  final TonightSkyObject object;
  final BestType type;

  /// Its dark window is open now.
  final bool upNow;

  /// Below the horizon at the rank instant.
  final bool belowHorizon;
  final bool inQueue;
  final bool collected;

  /// Set only on a Target the Moon affects; null draws nothing.
  final double? moonSeparationDeg;
}

/// The ranker's moon-full separation: past it the Moon does not hurt.
const _moonAffectsBelowDeg = 60;

/// True when any row has a known framing: the field is configured.
bool fieldKnown(List<TonightSkyObject> ranked) =>
    ranked.any((o) => o.framing != TonightFraming.unknown);

/// [ranked] as the person chose to see it. Never re-ranks: Score keeps the
/// ranker's order, and every sort is stable. Rows go only for a Target that
/// never rises (cut as the plan sheet's tiles cut it) and for the filters.
/// [queued] and [collected] are raw Target names, matched by designation
/// against every id, alias and name of the row.
List<BestRow> bestRows(
  List<TonightSkyObject> ranked,
  BestChoices choices, {
  required DateTime nowUtc,
  Iterable<String> queued = const [],
  Iterable<String> collected = const [],
}) {
  final queuedKeys = {for (final n in queued) designationKey(n)};
  final collectedKeys = {for (final n in collected) designationKey(n)};
  bool any(Set<String> keys, TonightSkyObject o) =>
      [o.id, ...o.aliases, o.name].any((n) => keys.contains(designationKey(n)));
  // A filtered-out field is not applied when no row knows it.
  final useField = choices.fitsField && fieldKnown(ranked);
  final rows = <BestRow>[];
  for (final o in ranked) {
    if (o.maxAltitudeDeg <= 0) continue;
    final type = bestTypeOf(o.type);
    final up = windowStateFor(o, nowUtc) == TonightWindowState.open;
    if (choices.types.isNotEmpty && !choices.types.contains(type)) continue;
    if (choices.show == BestShow.upNow && !up) continue;
    if (o.maxAltitudeDeg < choices.minPeakAltDeg) continue;
    final from = choices.windowFromMin;
    final to = choices.windowToMin;
    if (from != null &&
        to != null &&
        !windowOverlapsRange(o.windowStartUtc, o.windowEndUtc, from, to)) {
      continue;
    }
    if (useField &&
        o.framing != TonightFraming.goodFit &&
        o.framing != TonightFraming.good) {
      continue;
    }
    final sep = o.moonSeparationDeg;
    final moonHurts =
        sep != null &&
        sep < _moonAffectsBelowDeg &&
        (o.moonUpFraction ?? 0) > 0 &&
        (o.moonIlluminationPct ?? 0) > 0;
    rows.add(
      BestRow(
        object: o,
        type: type,
        upNow: up,
        // Altitude at the rank instant (at most one sky tick old). Not the
        // flat defaultHorizonAltitudeDeg nor the custom skyline: the row
        // carries no azimuth to test a skyline against.
        belowHorizon: o.altitudeDeg < 0,
        inQueue: any(queuedKeys, o),
        collected: any(collectedKeys, o),
        moonSeparationDeg: moonHurts ? sep : null,
      ),
    );
  }
  // List.sort is not stable; sorting indexed pairs keeps ties in ranker order.
  final order = [for (final (i, r) in rows.indexed) (i, r)];
  int cmp((int, BestRow) a, (int, BestRow) b) {
    final c = switch (choices.sort) {
      BestSort.score => 0,
      BestSort.transit => switch ((
        a.$2.object.transitUtc,
        b.$2.object.transitUtc,
      )) {
        (null, null) => 0,
        (null, _) => 1,
        (_, null) => -1,
        (final x?, final y?) => x.compareTo(y),
      },
      BestSort.altitudeNow => b.$2.object.altitudeDeg.compareTo(
        a.$2.object.altitudeDeg,
      ),
    };
    return c != 0 ? c : a.$1.compareTo(b.$1);
  }

  order.sort(cmp);
  return [for (final e in order) e.$2];
}

/// Saves [BestChoices] to `tonights_best.json` in app support: this device
/// only, apart from the synced planning prefs.
class TonightsBestPrefsService {
  TonightsBestPrefsService({Future<Directory> Function()? supportDir})
    : _supportDir = supportDir ?? getApplicationSupportDirectory;

  final Future<Directory> Function() _supportDir;
  static const _fileName = 'tonights_best.json';
  Future<void> _chain = Future<void>.value();

  Future<File> _file() async =>
      File('${(await _supportDir()).path}/$_fileName');

  /// The saved choices; defaults on a missing file or any read error.
  Future<BestChoices> load() async {
    try {
      final f = await _file();
      if (!await f.exists()) return const BestChoices();
      return BestChoices.fromJson(jsonDecode(await f.readAsString()));
    } catch (_) {
      return const BestChoices();
    }
  }

  Future<void> save(BestChoices c) {
    final task = _chain.then((_) async {
      try {
        await (await _file()).writeAsString(
          jsonEncode(c.toJson()),
          flush: true,
        );
      } catch (_) {
        /* best effort: a failed prefs write must not break the UI */
      }
    });
    _chain = task;
    return task;
  }
}

final tonightsBestPrefsServiceProvider = Provider<TonightsBestPrefsService>(
  (ref) => TonightsBestPrefsService(),
);

class TonightsBestChoicesNotifier extends Notifier<BestChoices> {
  bool _touched = false;
  final _ready = Completer<void>();
  Future<void> _saved = Future<void>.value();

  /// Completes once the saved choices are applied (or the load failed).
  Future<void> get ready => _ready.future;

  /// Completes when the last write lands.
  Future<void> get saved => _saved;

  @override
  BestChoices build() {
    unawaited(_restore());
    return const BestChoices();
  }

  Future<void> _restore() async {
    try {
      final loaded = await ref.read(tonightsBestPrefsServiceProvider).load();
      // A change that raced the load wins.
      if (!_touched && ref.mounted) state = loaded;
    } finally {
      if (!_ready.isCompleted) _ready.complete();
    }
  }

  void _set(BestChoices c) {
    _touched = true;
    state = c;
    _saved = ref.read(tonightsBestPrefsServiceProvider).save(c);
  }

  void setSort(BestSort s) => _set(state.copyWith(sort: s));
  void setShow(BestShow s) => _set(state.copyWith(show: s));
  void setMinPeakAlt(int deg) => _set(state.copyWith(minPeakAltDeg: deg));
  void setFitsField(bool on) => _set(state.copyWith(fitsField: on));

  /// Both ends, minutes of the local day.
  void setWindow(int fromMin, int toMin) =>
      _set(state.copyWith(windowFromMin: fromMin, windowToMin: toMin));
  void clearWindow() => _set(state.copyWith(clearWindow: true));
  void clearTypes() => _set(state.copyWith(types: const {}));

  void toggleType(BestType t) {
    final next = {...state.types};
    if (!next.remove(t)) next.add(t);
    _set(state.copyWith(types: next));
  }

  /// Resets the five filters (types, Show, Peak altitude, Fits my field,
  /// Imaging hours); Sort stays.
  void clearFilters() => _set(
    state.copyWith(
      types: const {},
      show: BestShow.allNight,
      minPeakAltDeg: 0,
      fitsField: false,
      clearWindow: true,
    ),
  );
}

final tonightsBestChoicesProvider =
    NotifierProvider<TonightsBestChoicesNotifier, BestChoices>(
      TonightsBestChoicesNotifier.new,
    );

/// The list the screen shows. It follows [tonightsBestProvider], which
/// re-ranks on each sky clock tick by itself, so no listener of its own.
final tonightsBestListProvider = Provider<AsyncValue<List<BestRow>>>((ref) {
  final choices = ref.watch(tonightsBestChoicesProvider);
  final ranked = ref.watch(tonightsBestProvider);
  final queued = ref.watch(
    simpleRunProvider.select((r) => [for (final t in r.targets) t.name]),
  );
  final collected = ref
      .watch(statsTargetsProvider)
      .maybeWhen(
        data: (l) => [for (final t in l ?? <StatsTarget>[]) t.targetName],
        orElse: () => const <String>[],
      );
  final nowUtc = ref.read(clockProvider)().toUtc();
  return ranked.whenData(
    (list) => bestRows(
      list,
      choices,
      nowUtc: nowUtc,
      queued: queued,
      collected: collected,
    ),
  );
});
