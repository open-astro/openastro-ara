/// Sky Album matching, aggregates and tiers (docs/specs/sky-album-v1.md,
/// "Matching rule" and "Client / State"). Pure; unit-tested.
library;

import 'dart:math' as math;

import '../../services/dso_catalog_service.dart';
import '../../util/dedupe_catalog.dart';
import 'album_frame.dart';

enum AlbumBook { messier, caldwell }

enum AlbumTier { bronze, silver, gold }

/// A Book's objects from the full bundled catalog, in catalog-number order.
List<PlanningDso> bookObjects(List<PlanningDso> catalog, AlbumBook book) {
  int? numOf(PlanningDso d) => book == AlbumBook.messier ? d.messierNum : d.caldwellNum;
  return [for (final d in catalog) if (numOf(d) != null) d]..sort((a, b) => numOf(a)!.compareTo(numOf(b)!));
}

/// Whether [f] contains [o]: the object lies within the frame's inscribed
/// circle, half the short side across. Rotation-free, so corners never count.
/// False for a frame without pointing or pixel scale (those use [targetNames]).
bool frameContains(AlbumFrame f, PlanningDso o) {
  if (!f.hasPosition) return false;
  final radiusArcmin = math.min(f.width, f.height) * f.pixelScaleArcsec! / 2 / 60;
  // 1e-9': the edge itself counts, whatever the last bit of the trigonometry says.
  return separationArcmin(f.raDeg!, f.decDeg!, o.raDeg, o.decDeg) <= radiusArcmin + 1e-9;
}

/// The alias fallback: [target] (a frame's target name) names [o] when it
/// equals the object's id, name or an alias after folding case, spacing, zero
/// padding and the words Messier / Caldwell, so M31, M 31 and Messier 31 match.
bool targetNames(String target, PlanningDso o) {
  final key = _albumKey(target);
  if (key.isEmpty) return false;
  return [o.id, o.name, ...o.aliases].any((d) => _albumKey(d) == key);
}

final RegExp _catalogWord = RegExp(r'^\s*(messier|caldwell)', caseSensitive: false);

String _albumKey(String s) => designationKey(
    s.replaceFirstMapped(_catalogWord, (m) => m[1]!.toLowerCase() == 'messier' ? 'M' : 'C'));

/// None below one light; bronze at one, silver at 1 h, gold at 5 h, any filter.
AlbumTier? tierFor({required int lights, required double hours}) {
  if (lights < 1) return null;
  if (hours >= 5) return AlbumTier.gold;
  if (hours >= 1) return AlbumTier.silver;
  return AlbumTier.bronze;
}

/// One Card's figures, derived from the lights that contain its object.
class CardStats {
  final int frameCount;
  final double integrationHours;
  final DateTime? firstCaptureUtc;
  final String bestFrameId;

  /// The best frame's composite score, null when no frame is scored yet.
  final double? bestScore;

  /// The best frame's gear profile id; the screen resolves it to a name.
  final String? profileId;
  final List<String> filters;

  const CardStats({
    required this.frameCount,
    required this.integrationHours,
    required this.firstCaptureUtc,
    required this.bestFrameId,
    this.bestScore,
    required this.profileId,
    required this.filters,
  });

  AlbumTier? get tier => tierFor(lights: frameCount, hours: integrationHours);
}

/// Per-object stats for a Book, keyed by object id; an object no frame
/// contains has no entry. A frame with pointing and scale credits every object
/// it contains; one without falls back to its target name.
Map<String, CardStats> aggregateBook(Iterable<AlbumFrame> frames, List<PlanningDso> book) {
  final epoch = DateTime.utc(0);
  final ordered = frames.toList()
    // frameId breaks capture-time ties so the best frame doesn't depend on the reply's order.
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
    for (final MapEntry(key: id, value: fs) in hits.entries) id: _stats(fs),
  };
}

CardStats _stats(List<AlbumFrame> fs) {
  // fs is in capture order: the last of equal scores is the latest capture.
  var best = fs.first;
  for (final f in fs) {
    if ((f.compositeScore ?? -1) >= (best.compositeScore ?? -1)) best = f;
  }
  final filters = <String>[];
  for (final f in fs) {
    final name = f.filterName;
    if (name != null && !filters.contains(name)) filters.add(name);
  }
  return CardStats(
    frameCount: fs.length,
    integrationHours: fs.fold(0.0, (h, f) => h + f.exposureSeconds) / 3600,
    firstCaptureUtc: fs.map((f) => f.capturedUtc).nonNulls.firstOrNull,
    bestFrameId: best.frameId,
    bestScore: best.compositeScore,
    profileId: best.profileId,
    filters: filters,
  );
}

/// [cache] with [rows] merged in by frame id (a `since` reply carries changed
/// rows that may already be cached).
Map<String, AlbumFrame> mergeAlbumRows(Map<String, AlbumFrame> cache, Iterable<AlbumFrame> rows) =>
    {...cache, for (final r in rows) r.frameId: r};

/// The `since` for the next fetch: the newest updated time held, or null.
DateTime? albumSince(Map<String, AlbumFrame> cache) {
  DateTime? newest;
  for (final f in cache.values) {
    final u = f.updatedUtc;
    if (u != null && (newest == null || u.isAfter(newest))) newest = u;
  }
  return newest;
}

/// A merged count that differs from the daemon's total means rows were
/// deleted (or missed): refetch everything.
bool needsFullReload(Map<String, AlbumFrame> merged, {required int totalLights}) => merged.length != totalLights;
