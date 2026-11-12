/// Sky Album matching, aggregates and tiers (docs/specs/sky-album-v1.md,
/// "Matching rule" and "Client / State"). Pure; unit-tested.
library;

import 'dart:math' as math;

import '../../models/preflight_checklist.dart';
import '../../services/dso_catalog_service.dart';
import '../../util/dedupe_catalog.dart';
import 'album_art.dart';
import 'album_frame.dart';

/// The Books in display order. An object is a Card in every Book that claims it (RCW
/// overlaps Messier and Caldwell); with several shown, it shows as its first Book's
/// Card. A fourth Book is one more value here.
enum AlbumBook { messier, caldwell, rcw }

enum AlbumTier { bronze, silver, gold }

final RegExp _rcwDesignation = RegExp(r'^RCW (\d+)$');

/// The RCW number of [d]: the lowest `RCW n` among its id and aliases (a nebula that
/// carries two RCW numbers is one Card, labelled by the first), null without one.
/// Worked out once per row: until its Books are matched the Album asks it of every
/// catalog row on each build.
int? rcwNumOf(PlanningDso d) => (_rcwNums[d] ??= (_scanRcwNum(d),)).$1;

final Expando<(int?,)> _rcwNums = Expando('rcwNum');

int? _scanRcwNum(PlanningDso d) {
  int? best;
  for (final s in [d.id, ...d.aliases]) {
    final n = int.tryParse(_rcwDesignation.firstMatch(s)?.group(1) ?? '');
    if (n != null && (best == null || n < best)) best = n;
  }
  return best;
}

/// [d]'s catalog number in [book], null when the Book does not claim it.
int? bookNumberOf(PlanningDso d, AlbumBook book) => switch (book) {
      AlbumBook.messier => d.messierNum,
      AlbumBook.caldwell => d.caldwellNum,
      AlbumBook.rcw => rcwNumOf(d),
    };

/// The Card label: `M 31`, `C 14`, `RCW 32`.
String bookLabel(PlanningDso d, AlbumBook book) => switch (book) {
      AlbumBook.messier => 'M ${d.messierNum}',
      AlbumBook.caldwell => 'C ${d.caldwellNum}',
      AlbumBook.rcw => 'RCW ${rcwNumOf(d)}',
    };

/// A Book's Cards: everything [book] claims in the full bundled catalog, in
/// catalog-number order, whatever another Book also claims. The grid, the chips and
/// the aggregates all read this, so their counts agree.
List<PlanningDso> bookObjects(List<PlanningDso> catalog, AlbumBook book) =>
    [for (final d in catalog) if (bookNumberOf(d, book) != null) d]
      ..sort((a, b) => bookNumberOf(a, book)!.compareTo(bookNumberOf(b, book)!));

/// Whether [f] contains [o]: the object lies within the frame's inscribed
/// circle, half the short side across. Rotation-free, so corners never count.
/// False for a frame without pointing or pixel scale (those use [targetNames]).
bool frameContains(AlbumFrame f, PlanningDso o) {
  if (!f.hasPosition) return false;
  final radiusArcmin =
      math.min(f.width, f.height) * f.pixelScaleArcsec! / 2 / 60;
  // 1e-9': the edge itself counts, whatever the last bit of the trigonometry says.
  return separationArcmin(f.raDeg!, f.decDeg!, o.raDeg, o.decDeg) <=
      radiusArcmin + 1e-9;
}

/// The alias fallback: [target] (a frame's target name) names [o] when it
/// equals the object's id, name or an alias after folding case, spacing, zero
/// padding and the words Messier / Caldwell, so M31, M 31 and Messier 31 match.
bool targetNames(String target, PlanningDso o) {
  final key = _albumKey(target);
  if (key.isEmpty) return false;
  return [o.id, o.name, ...o.aliases].any((d) => _albumKey(d) == key);
}

/// The Art of the object the sky picked: the Messier or Caldwell catalog
/// object that one of [designations] / [name] names (as [targetNames]
/// folds), and only when its manifest row has art. Null otherwise.
AlbumArt? skyPickArt(
  Iterable<String> designations,
  String name,
  List<PlanningDso> catalog,
  Map<String, AlbumArt> manifest,
) {
  // The engine prefixes common names ("NAME Orion Nebula").
  final names = [
    for (final d in [...designations, name])
      d.replaceFirst(RegExp(r'^NAME\s+'), ''),
  ];
  for (final o in catalog) {
    final num = o.messierNum != null
        ? 'M${o.messierNum}'
        : (o.caldwellNum != null ? 'C${o.caldwellNum}' : null);
    if (num == null || !names.any((n) => targetNames(n, o))) continue;
    final art = manifest[num];
    return art?.asset == null ? null : art;
  }
  return null;
}

final RegExp _catalogWord = RegExp(
  r'^\s*(messier|caldwell)',
  caseSensitive: false,
);

String _albumKey(String s) => designationKey(
  s.replaceFirstMapped(
    _catalogWord,
    (m) => m[1]!.toLowerCase() == 'messier' ? 'M' : 'C',
  ),
);

/// Hours of lights, any filter, that make a Card silver and gold.
const double albumSilverHours = 1;
const double albumGoldHours = 5;

/// None below one light; bronze at one, silver at 1 h, gold at 5 h, any filter.
AlbumTier? tierFor({required int lights, required double hours}) {
  if (lights < 1) return null;
  if (hours >= albumGoldHours) return AlbumTier.gold;
  if (hours >= albumSilverHours) return AlbumTier.silver;
  return AlbumTier.bronze;
}

/// One observing Night of a Card: its lights, the most common exposure
/// length (the "sub") and the hours.
class AlbumNight {
  /// The evening's local date, `yyyy-mm-dd`, by the noon rule.
  final String key;
  final int lights;
  final double subSeconds;
  final double hours;
  const AlbumNight({
    required this.key,
    required this.lights,
    required this.subSeconds,
    required this.hours,
  });
}

/// One Card's figures, derived from the lights that contain its object.
class CardStats {
  final int frameCount;
  final double integrationHours;
  final DateTime? firstCaptureUtc;
  final List<String> filters;

  /// Newest first; lights with no capture time are in the totals only.
  final List<AlbumNight> nights;

  const CardStats({
    required this.frameCount,
    required this.integrationHours,
    required this.firstCaptureUtc,
    required this.filters,
    this.nights = const [],
  });

  AlbumTier? get tier => tierFor(lights: frameCount, hours: integrationHours);
}

/// Per-object stats for a Book, keyed by object id; an object no frame
/// contains has no entry. A frame with pointing and scale credits every object
/// it contains; one without falls back to its target name.
Map<String, CardStats> aggregateBook(
  Iterable<AlbumFrame> frames,
  List<PlanningDso> book, {
  double? siteLongitudeDeg,
}) {
  final epoch = DateTime.utc(0);
  final ordered = frames.toList()
    // frameId breaks capture-time ties so the order doesn't depend on the reply's.
    ..sort((a, b) {
      final t = (a.capturedUtc ?? epoch).compareTo(b.capturedUtc ?? epoch);
      return t != 0 ? t : a.frameId.compareTo(b.frameId);
    });
  final hits = <String, List<AlbumFrame>>{};
  for (final f in ordered) {
    for (final o in book) {
      if (f.hasPosition ? frameContains(f, o) : targetNames(f.targetName, o)) {
        hits.putIfAbsent(o.id, () => []).add(f);
      }
    }
  }
  return {
    for (final MapEntry(key: id, value: fs) in hits.entries) id: _stats(fs, siteLongitudeDeg),
  };
}

CardStats _stats(List<AlbumFrame> fs, double? siteLongitudeDeg) {
  final filters = <String>[];
  for (final f in fs) {
    final name = f.filterName;
    if (name != null && !filters.contains(name)) filters.add(name);
  }
  return CardStats(
    frameCount: fs.length,
    integrationHours: fs.fold(0.0, (h, f) => h + f.exposureSeconds) / 3600,
    firstCaptureUtc: fs.map((f) => f.capturedUtc).nonNulls.firstOrNull,
    filters: filters,
    nights: albumNights(fs, siteLongitudeDeg: siteLongitudeDeg),
  );
}

/// [frames] grouped by Night (the `preflightNightKey` noon rule), newest first.
///
/// The clock the rule reads is the site's: with [siteLongitudeDeg] (from the profile's site) it is
/// UTC plus the solar offset, longitude / 15 h. The client has no zone database (the profile's
/// IANA zone name cannot be turned into an offset here), so the longitude wins; without a site
/// the device's local time stands in.
List<AlbumNight> albumNights(Iterable<AlbumFrame> frames, {double? siteLongitudeDeg}) {
  DateTime siteClock(DateTime utc) => siteLongitudeDeg == null
      ? utc.toLocal()
      : utc.add(Duration(minutes: (siteLongitudeDeg * 4).round())).copyWith(isUtc: false);
  final byNight = <String, List<AlbumFrame>>{};
  for (final f in frames) {
    final t = f.capturedUtc;
    if (t != null) {
      byNight.putIfAbsent(preflightNightKey(siteClock(t)), () => []).add(f);
    }
  }
  final keys = byNight.keys.toList()..sort((a, b) => b.compareTo(a));
  return [
    for (final k in keys)
      AlbumNight(
        key: k,
        lights: byNight[k]!.length,
        subSeconds: _commonest(byNight[k]!.map((f) => f.exposureSeconds)),
        hours: byNight[k]!.fold(0.0, (h, f) => h + f.exposureSeconds) / 3600,
      ),
  ];
}

/// The most common value; the longer one on a tie, so the answer doesn't depend on order.
double _commonest(Iterable<double> values) {
  final counts = <double, int>{};
  for (final v in values) {
    counts[v] = (counts[v] ?? 0) + 1;
  }
  return counts.entries
      .reduce(
        (a, b) =>
            b.value > a.value || (b.value == a.value && b.key > a.key) ? b : a,
      )
      .key;
}

/// [cache] with [rows] merged in by frame id: a row replaces the cached one
/// unless it is older (a pointing backfill can add objects to a frame).
/// Nothing is removed: the collection is a high-water mark (ADR 0001).
Map<String, AlbumFrame> mergeAlbumRows(
  Map<String, AlbumFrame> cache,
  Iterable<AlbumFrame> rows,
) {
  final out = {...cache};
  for (final r in rows) {
    final held = out[r.frameId]?.updatedUtc;
    final next = r.updatedUtc;
    if (held == null || (next != null && !next.isBefore(held))) {
      out[r.frameId] = r;
    }
  }
  return out;
}

/// The newest updated time among [rows], or null: the next fetch's `since`.
DateTime? albumSince(Iterable<AlbumFrame> rows) {
  DateTime? newest;
  for (final f in rows) {
    final u = f.updatedUtc;
    if (u != null && (newest == null || u.isAfter(newest))) newest = u;
  }
  return newest;
}
