import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/services/dso_catalog_service.dart';
import 'package:openastroara/state/album/album_frame.dart';
import 'package:openastroara/state/album/album_matching.dart';

/// Sky Album slice 4 (#1519, docs/specs/sky-album-v1.md "Matching rule" and
/// "Client / State"): pure matching, aggregates, tiers and the reload rule.

PlanningDso _dso(String id, {String? name, double ra = 10, double dec = 20, int? m, int? c, List<String> aliases = const []}) =>
    PlanningDso(
      id: id,
      name: name ?? id,
      type: 'G',
      magnitude: 5,
      raDeg: ra,
      decDeg: dec,
      messierNum: m,
      caldwellNum: c,
      aliases: aliases,
    );

AlbumFrame _frame(
  String id, {
  double? ra = 10,
  double? dec = 20,
  double? scale = 1.0,
  int width = 1000,
  int height = 600,
  String target = 'M 31',
  double exposure = 300,
  String? filter,
  double? score,
  DateTime? captured,
  String? profile,
  DateTime? updated,
}) =>
    AlbumFrame(
      frameId: id,
      raDeg: ra,
      decDeg: dec,
      width: width,
      height: height,
      pixelScaleArcsec: scale,
      capturedUtc: captured ?? DateTime.utc(2026, 3, 2),
      exposureSeconds: exposure,
      filterName: filter,
      targetName: target,
      compositeScore: score,
      profileId: profile,
      updatedUtc: updated,
    );

void main() {
  group('containment: the inscribed circle', () {
    // 1000 x 600 px at 1"/px: the short side is 600", so the circle's radius is 5'.
    final frame = _frame('f');
    test('an object at the centre is contained', () {
      expect(frameContains(frame, _dso('a')), isTrue);
    });
    test('an object at half the short side is contained', () {
      expect(frameContains(frame, _dso('a', dec: 20 + 5 / 60)), isTrue);
    });
    test('an object just outside the circle is not', () {
      expect(frameContains(frame, _dso('a', dec: 20 + 5.05 / 60)), isFalse);
    });
    test('an object in a frame corner is not credited', () {
      // 500" east and 300" north of the centre: inside the rectangle, outside the circle.
      expect(frameContains(frame, _dso('a', ra: 10 + 500 / 3600 / 0.9396926, dec: 20 + 300 / 3600)), isFalse);
    });
    test('a frame without pointing or scale contains nothing by position', () {
      expect(frameContains(_frame('f', ra: null), _dso('a')), isFalse);
      expect(frameContains(_frame('f', scale: null), _dso('a')), isFalse);
    });
  });

  group('alias fallback', () {
    final m31 = _dso('NGC0224', name: 'Andromeda Galaxy', m: 31, aliases: const ['M 31']);
    for (final target in ['M31', 'M 31', 'Messier 31', 'messier31', 'NGC 224', 'Andromeda Galaxy']) {
      test('"$target" names M31', () => expect(targetNames(target, m31), isTrue));
    }
    test('another object is not matched', () => expect(targetNames('M 32', m31), isFalse));
    test('Caldwell folds like Messier', () {
      expect(targetNames('Caldwell 14', _dso('NGC0869', c: 14, aliases: const ['C 014'])), isTrue);
    });
  });

  group('tiers', () {
    test('none below one light, bronze at one, silver at 1 h, gold at 5 h', () {
      expect(tierFor(lights: 0, hours: 0), isNull);
      expect(tierFor(lights: 1, hours: 0.01), AlbumTier.bronze);
      expect(tierFor(lights: 12, hours: 0.999), AlbumTier.bronze);
      expect(tierFor(lights: 12, hours: 1), AlbumTier.silver);
      expect(tierFor(lights: 60, hours: 4.999), AlbumTier.silver);
      expect(tierFor(lights: 60, hours: 5), AlbumTier.gold);
    });
  });

  group('aggregates', () {
    final m81 = _dso('NGC3031', ra: 148.888, dec: 69.065, m: 81, aliases: const ['M 81']);
    final m82 = _dso('NGC3034', ra: 148.970, dec: 69.680, m: 82);
    // 3000 x 2000 px at 2"/px: a 33.3' radius covers both galaxies from a centre between them.
    final wide = {'ra': 148.93, 'dec': 69.37, 'scale': 2.0, 'w': 3000, 'h': 2000};

    test('every contained object gets full credit, best frame, filters and first capture', () {
      final frames = [
        _frame('late', ra: wide['ra'] as double, dec: wide['dec'] as double, scale: 2, width: 3000, height: 2000,
            exposure: 1800, filter: 'Ha', score: 0.9, captured: DateTime.utc(2026, 4, 2), profile: 'p2'),
        _frame('early', ra: wide['ra'] as double, dec: wide['dec'] as double, scale: 2, width: 3000, height: 2000,
            exposure: 1800, filter: 'L', score: 0.9, captured: DateTime.utc(2026, 3, 1), profile: 'p1'),
        _frame('named', ra: null, target: 'M81', exposure: 600, filter: 'Ha', score: 0.5,
            captured: DateTime.utc(2026, 5, 1)),
      ];
      final stats = aggregateBook(frames, [m81, m82]);

      final s81 = stats['NGC3031']!;
      expect(s81.frameCount, 3);
      expect(s81.integrationHours, closeTo(4200 / 3600, 1e-9));
      expect(s81.firstCaptureUtc, DateTime.utc(2026, 3, 1));
      expect(s81.bestFrameId, 'late', reason: 'equal scores: the later capture wins');
      expect(s81.profileId, 'p2');
      expect(s81.filters, ['L', 'Ha'], reason: 'distinct, in capture order');
      expect(s81.tier, AlbumTier.silver);

      final s82 = stats['NGC3034']!;
      expect(s82.frameCount, 2, reason: 'the named frame credits M81 only');
      expect(s82.integrationHours, 1.0);
    });

    test('an object no frame contains has no stats', () {
      expect(aggregateBook([_frame('x', target: 'M 1')], [m81]), isEmpty);
    });
  });

  group('merge and reload rule', () {
    final a = _frame('a', updated: DateTime.utc(2026, 3, 1));
    final b = _frame('b', updated: DateTime.utc(2026, 3, 2));

    test('rows merge by frame id and the newest updated time is the next since', () {
      final merged = mergeAlbumRows({'a': a}, [b, _frame('a', exposure: 60, updated: DateTime.utc(2026, 3, 3))]);
      expect(merged.keys, unorderedEquals(['a', 'b']));
      expect(merged['a']!.exposureSeconds, 60);
      expect(albumSince(merged), DateTime.utc(2026, 3, 3));
      expect(albumSince(const {}), isNull);
    });

    test('a since reply that only re-delivers cached rows keeps the count', () {
      final merged = mergeAlbumRows({'a': a, 'b': b}, [b]);
      expect(needsFullReload(merged, totalLights: 2), isFalse);
    });

    test('a count that differs from totalLights asks for a full reload (a deletion)', () {
      final merged = mergeAlbumRows({'a': a, 'b': b}, const []);
      expect(needsFullReload(merged, totalLights: 1), isTrue);
    });
  });

  group('books', () {
    test('a Book lists its catalog numbers in order', () {
      final catalog = [_dso('x', m: 2), _dso('y', c: 1), _dso('z', m: 1), _dso('w')];
      expect(bookObjects(catalog, AlbumBook.messier).map((d) => d.id), ['z', 'x']);
      expect(bookObjects(catalog, AlbumBook.caldwell).map((d) => d.id), ['y']);
    });
  });

  test('AlbumFrame parses the snake_case wire row', () {
    final f = AlbumFrame.fromJson({
      'frame_id': 'id1', 'ra_deg': 10.68, 'dec_deg': 41.27, 'width': 4144, 'height': 2822, 'bin': 1,
      'pixel_scale_arcsec': 1.56, 'captured_utc': '2026-03-02T11:20:31Z', 'exposure_seconds': 180,
      'filter_name': 'Ha', 'target_name': 'M31', 'composite_score': 0.83, 'profile_id': 'p',
      'updated_utc': '2026-03-03T00:00:00Z',
    });
    expect((f.frameId, f.raDeg, f.width, f.pixelScaleArcsec, f.exposureSeconds, f.targetName, f.compositeScore),
        ('id1', 10.68, 4144, 1.56, 180.0, 'M31', 0.83));
    expect(f.capturedUtc, DateTime.utc(2026, 3, 2, 11, 20, 31));
    expect(f.updatedUtc, DateTime.utc(2026, 3, 3));
  });
}
