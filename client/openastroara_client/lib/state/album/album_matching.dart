/// Sky Album matching, aggregates and tiers (docs/specs/sky-album-v1.md,
/// "Matching rule" and "Client / State"). Pure; unit-tested.
library;

import 'dart:math' as math;

import 'package:collection/collection.dart' show lowerBound;

import '../../models/preflight_checklist.dart';
import '../../services/dso_catalog_service.dart';
import '../../util/dedupe_catalog.dart';
import 'album_art.dart';
import 'album_frame.dart';

/// The Books in display order. An object is a Card in every Book that claims it (RCW
/// overlaps Messier and Caldwell, NGC overlaps all three); with several shown, it shows
/// as its first Book's Card. One more Book is one more value here, its claim pattern and
/// its label.
enum AlbumBook { messier, caldwell, rcw, ngc, ic, sharpless }

enum AlbumTier { bronze, silver, gold }

/// What a catalog-file Book claims through: `RCW n`, `NGC n[A]`, `IC n[A]`, `Sh2-n`, on the
/// id or an alias, zero padding and a space after the prefix tolerated.
final Map<AlbumBook, RegExp> _claimPatterns = {
  AlbumBook.rcw: RegExp(r'^RCW\s?0*(\d+)()$'),
  AlbumBook.ngc: RegExp(r'^NGC\s?0*(\d+)([A-Z]?)$'),
  AlbumBook.ic: RegExp(r'^IC\s?0*(\d+)([A-Z]?)$'),
  AlbumBook.sharpless: RegExp(r'^Sh2-0*(\d+)()$'),
};

/// [d]'s number and letter suffix in [book] ("" without one).
typedef _Claim = ({int number, String suffix});

/// Worked out once per row and Book: until its Books are matched the Album asks it of
/// every catalog row on each build.
final Map<AlbumBook, Expando<(_Claim?,)>> _claims = {
  for (final b in _claimPatterns.keys) b: Expando('claim ${b.name}'),
};

_Claim? _claimOf(PlanningDso d, AlbumBook book) =>
    (_claims[book]![d] ??= (_scanClaim(d, _claimPatterns[book]!),)).$1;

/// The lowest number among [d]'s id and aliases (a row that carries two numbers in a
/// Book is one Card, labelled by the first); the plain number sorts before its suffixes.
_Claim? _scanClaim(PlanningDso d, RegExp pattern) {
  _Claim? best;
  for (final s in [d.id, ...d.aliases]) {
    final m = pattern.firstMatch(s);
    if (m == null) continue;
    final c = (number: int.parse(m[1]!), suffix: m[2]!);
    if (best == null || _compareClaims(c, best) < 0) best = c;
  }
  return best;
}

int _compareClaims(_Claim a, _Claim b) => a.number != b.number
    ? a.number.compareTo(b.number)
    : a.suffix.compareTo(b.suffix);

/// The RCW number of [d]: the lowest `RCW n` among its id and aliases, null without one.
int? rcwNumOf(PlanningDso d) => _claimOf(d, AlbumBook.rcw)?.number;

/// [d]'s catalog number in [book], null when the Book does not claim it.
int? bookNumberOf(PlanningDso d, AlbumBook book) => switch (book) {
  AlbumBook.messier => d.messierNum,
  AlbumBook.caldwell => d.caldwellNum,
  _ => _claimOf(d, book)?.number,
};

/// The letter after [bookNumberOf] (`NGC 5194A`): "" for none and for the Books without suffixes.
String bookSuffixOf(PlanningDso d, AlbumBook book) =>
    _claims[book] == null ? '' : _claimOf(d, book)?.suffix ?? '';

/// The Card label: `M 31`, `C 14`, `RCW 32`, `NGC 224`, `IC 434`, `Sh2-101`.
String bookLabel(PlanningDso d, AlbumBook book) {
  final n = '${bookNumberOf(d, book)}${bookSuffixOf(d, book)}';
  return switch (book) {
    AlbumBook.messier => 'M $n',
    AlbumBook.caldwell => 'C $n',
    AlbumBook.rcw => 'RCW $n',
    AlbumBook.ngc => 'NGC $n',
    AlbumBook.ic => 'IC $n',
    AlbumBook.sharpless => 'Sh2-$n',
  };
}

/// A Book's Cards: everything [book] claims in the full bundled catalog, in
/// catalog-number order, whatever another Book also claims. The grid, the chips and
/// the aggregates all read this, so their counts agree.
List<PlanningDso> bookObjects(List<PlanningDso> catalog, AlbumBook book) =>
    [
      for (final d in catalog)
        if (bookNumberOf(d, book) != null) d,
    ]..sort((a, b) {
      final n = bookNumberOf(a, book)!.compareTo(bookNumberOf(b, book)!);
      return n != 0
          ? n
          : bookSuffixOf(a, book).compareTo(bookSuffixOf(b, book));
    });

/// Whether [f] contains [o]: the object lies within the frame's inscribed
/// circle, half the short side across. Rotation-free, so corners never count.
/// False for a frame without pointing or pixel scale (those use [targetNames]).
bool frameContains(AlbumFrame f, PlanningDso o) {
  if (!f.hasPosition) return false;
  return separationArcmin(f.raDeg!, f.decDeg!, o.raDeg, o.decDeg) <=
      _reachArcmin(f);
}

/// How far from a position frame's centre [frameContains] credits: half the short side, plus
/// 1e-9' so the edge itself counts, whatever the last bit of the trigonometry says.
double _reachArcmin(AlbumFrame f) =>
    math.min(f.width, f.height) * f.pixelScaleArcsec! / 2 / 60 + 1e-9;

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

/// Hours of lights, any filter, that make a Card bronze, silver and gold.
/// Bronze needs 15 min so a stray frame collects nothing (user, 2026-11-18).
const double albumBronzeHours = 0.25;
const double albumSilverHours = 1;
const double albumGoldHours = 5;

/// None below 15 min; bronze at 15 min, silver at 1 h, gold at 5 h, any filter.
AlbumTier? tierFor({required int lights, required double hours}) {
  if (lights < 1) return null;
  if (hours >= albumGoldHours) return AlbumTier.gold;
  if (hours >= albumSilverHours) return AlbumTier.silver;
  if (hours >= albumBronzeHours) return AlbumTier.bronze;
  return null;
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
  // Indexed once per call, each only if a frame needs it: testing all ~8k objects of a Book
  // against each of thousands of frames froze the Album.
  late final contained = _containedIn(book);
  late final named = _byTargetKey(book);
  final hits = <String, List<AlbumFrame>>{};
  for (final f in ordered) {
    for (final i
        in f.hasPosition
            ? contained(f)
            : named[_albumKey(f.targetName)] ?? const <int>[]) {
      hits.putIfAbsent(book[i].id, () => []).add(f);
    }
  }
  return {
    for (final MapEntry(key: id, value: fs) in hits.entries)
      id: _stats(fs, siteLongitudeDeg),
  };
}

/// The [book] indices, ascending, of the objects a position frame contains ([frameContains]).
/// Only the objects within its reach in declination are tested: no point of its circle is
/// further than that from the centre in declination.
List<int> Function(AlbumFrame) _containedIn(List<PlanningDso> book) {
  final byDec = [for (var i = 0; i < book.length; i++) i]
    ..sort((a, b) => book[a].decDeg.compareTo(book[b].decDeg));
  final decs = [for (final i in byDec) book[i].decDeg];
  // The band rule holds on the sphere only; a declination past a pole is corrupt, so tested
  // object by object, as before.
  final onSphere = decs.every((d) => d.abs() <= 90);
  return (f) {
    final dec = f.decDeg!;
    if (!onSphere || dec.abs() > 90) {
      return [
        for (var i = 0; i < book.length; i++)
          if (frameContains(f, book[i])) i,
      ];
    }
    // 1e-6 deg: far above the rounding of the trigonometry and of the bounds.
    final band = _reachArcmin(f) / 60 + 1e-6;
    return [
      for (
        var j = lowerBound(decs, dec - band);
        j < decs.length && decs[j] <= dec + band;
        j++
      )
        if (frameContains(f, book[byDec[j]])) byDec[j],
    ]..sort();
  };
}

/// The [book] indices, ascending, of the objects with an id, name or alias that [targetNames]
/// folds to each key: a name-only frame is one lookup.
Map<String, List<int>> _byTargetKey(List<PlanningDso> book) {
  final out = <String, List<int>>{};
  for (var i = 0; i < book.length; i++) {
    final o = book[i];
    // Each key once per object: a frame credits it once, however many designations fold alike.
    for (final k in {
      for (final d in [o.id, o.name, ...o.aliases]) _albumKey(d),
    }) {
      // An empty key names nothing ([targetNames]).
      if (k.isNotEmpty) out.putIfAbsent(k, () => []).add(i);
    }
  }
  return out;
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
List<AlbumNight> albumNights(
  Iterable<AlbumFrame> frames, {
  double? siteLongitudeDeg,
}) {
  DateTime siteClock(DateTime utc) => siteLongitudeDeg == null
      ? utc.toLocal()
      : utc
            .add(Duration(minutes: (siteLongitudeDeg * 4).round()))
            .copyWith(isUtc: false);
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
