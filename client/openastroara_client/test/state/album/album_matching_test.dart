import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/services/dso_catalog_service.dart';
import 'package:openastroara/state/album/album_art.dart';
import 'package:openastroara/state/album/album_frame.dart';
import 'package:openastroara/state/album/album_matching.dart';

/// Sky Album slice 4 (#1519, docs/specs/sky-album-v1.md "Matching rule" and
/// "Client / State"): pure matching, aggregates, tiers, Nights and the merge
/// rule of the collection cache (#1595, ADR 0001).

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
  DateTime? captured,
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

    test('every contained object gets full credit, filters and first capture', () {
      final frames = [
        _frame('late', ra: wide['ra'] as double, dec: wide['dec'] as double, scale: 2, width: 3000, height: 2000,
            exposure: 1800, filter: 'Ha', captured: DateTime.utc(2026, 4, 2)),
        _frame('early', ra: wide['ra'] as double, dec: wide['dec'] as double, scale: 2, width: 3000, height: 2000,
            exposure: 1800, filter: 'L', captured: DateTime.utc(2026, 3, 1)),
        _frame('named', ra: null, target: 'M81', exposure: 600, filter: 'Ha', captured: DateTime.utc(2026, 5, 1)),
      ];
      final stats = aggregateBook(frames, [m81, m82]);

      final s81 = stats['NGC3031']!;
      expect(s81.frameCount, 3);
      expect(s81.integrationHours, closeTo(4200 / 3600, 1e-9));
      expect(s81.firstCaptureUtc, DateTime.utc(2026, 3, 1));
      expect(s81.filters, ['L', 'Ha'], reason: 'distinct, in capture order');
      expect(s81.tier, AlbumTier.silver);

      final s82 = stats['NGC3034']!;
      expect(s82.frameCount, 2, reason: 'the named frame credits M81 only');
      expect(s82.integrationHours, 1.0);
    });

    test('a frame whose stored scale or size is unusable falls back to its target name', () {
      final frames = [_frame('zero', scale: 0, target: 'M81'), _frame('flat', width: 0, target: 'M81')];
      expect(aggregateBook(frames, [m81])['NGC3031']!.frameCount, 2);
    });

    test('first capture skips frames with no capture time', () {
      final frames = [_frame('a', ra: null, target: 'M81', captured: null), _frame('b', ra: null, target: 'M81')];
      final undated = AlbumFrame(frameId: 'u', targetName: 'M81', exposureSeconds: 60);
      expect(aggregateBook([undated, ...frames], [m81])['NGC3031']!.firstCaptureUtc, DateTime.utc(2026, 3, 2));
    });

    test('an object no frame contains has no stats', () {
      expect(aggregateBook([_frame('x', target: 'M 1')], [m81]), isEmpty);
    });
  });

  group('merge rule: a high-water mark', () {
    final a = _frame('a', updated: DateTime.utc(2026, 3, 1));
    final b = _frame('b', updated: DateTime.utc(2026, 3, 2));

    test('a newer updated time replaces the cached row; the newest is the next since', () {
      final merged = mergeAlbumRows({'a': a}, [b, _frame('a', exposure: 60, updated: DateTime.utc(2026, 3, 3))]);
      expect(merged.keys, unorderedEquals(['a', 'b']));
      expect(merged['a']!.exposureSeconds, 60);
      expect(albumSince(merged.values), DateTime.utc(2026, 3, 3));
      expect(albumSince(const []), isNull);
    });

    test('an older row never replaces a newer one', () {
      final merged = mergeAlbumRows({'a': a}, [_frame('a', exposure: 60, updated: DateTime.utc(2026, 2, 1))]);
      expect(merged['a']!.exposureSeconds, 300);
    });

    test('a row absent from a later reply stays; a reply with fewer rows deletes nothing', () {
      expect(mergeAlbumRows({'a': a, 'b': b}, [b]).keys, unorderedEquals(['a', 'b']));
      expect(mergeAlbumRows({'a': a, 'b': b}, const []).keys, unorderedEquals(['a', 'b']));
    });

    test('a Tier computed before a deletion on the rig is the same after it', () {
      final m31 = _dso('NGC0224', m: 31, aliases: const ['M 31']);
      final cache = {
        for (var i = 0; i < 12; i++) 'f$i': _frame('f$i', ra: null, exposure: 1800, updated: DateTime.utc(2026, 3, 1)),
      };
      final before = aggregateBook(cache.values, [m31])['NGC0224']!.tier;
      // The rig deleted ten frames: its next full reply carries two.
      final after = mergeAlbumRows(cache, cache.values.take(2));
      expect(before, AlbumTier.gold);
      expect(aggregateBook(after.values, [m31])['NGC0224']!.tier, before);
    });
  });

  group('Nights', () {
    test('a 01:00 local light belongs to the previous evening', () {
      final nights = albumNights([
        _frame('evening', captured: DateTime(2026, 3, 2, 22).toUtc()),
        _frame('small hours', captured: DateTime(2026, 3, 3, 1).toUtc()),
        _frame('next', captured: DateTime(2026, 3, 3, 21).toUtc()),
      ]);
      expect(nights.map((n) => (n.key, n.lights)), [('2026-03-03', 1), ('2026-03-02', 2)], reason: 'newest first');
    });

    test('the sub is the most common exposure of the Night, the longer on a tie', () {
      DateTime t(int m) => DateTime(2026, 3, 2, 22, m).toUtc();
      final night = albumNights([
        _frame('a', exposure: 180, captured: t(0)),
        _frame('b', exposure: 300, captured: t(5)),
        _frame('c', exposure: 180, captured: t(10)),
      ]).single;
      expect(night.subSeconds, 180);
      expect(night.hours, closeTo(660 / 3600, 1e-9));
      expect(albumNights([_frame('a', exposure: 60, captured: t(0)), _frame('b', exposure: 120, captured: t(1))])
          .single.subSeconds, 120);
    });

    test('a light with no capture time counts in the totals, not in any Night', () {
      final stats = aggregateBook([
        _frame('a', ra: null, target: 'M81'),
        const AlbumFrame(frameId: 'u', targetName: 'M81', exposureSeconds: 60),
      ], [_dso('NGC3031', m: 81, aliases: ['M 81'])])['NGC3031']!;
      expect(stats.frameCount, 2);
      expect(stats.nights.single.lights, 1);
    });
  });

  group('books', () {
    test('a Book lists its catalog numbers in order', () {
      final catalog = [_dso('x', m: 2), _dso('y', c: 1), _dso('z', m: 1), _dso('w')];
      expect(bookObjects(catalog, AlbumBook.messier).map((d) => d.id), ['z', 'x']);
      expect(bookObjects(catalog, AlbumBook.caldwell).map((d) => d.id), ['y']);
    });

    test('the RCW Book holds only RCW-prefixed objects, in catalog-number order, labelled RCW n (#1629)', () {
      final catalog = [
        _dso('NGC3372', aliases: ['RCW 53', 'Gum 33']),
        _dso('RCW 8'),
        _dso('Sh2-1'),
        _dso('NGC2359', aliases: ['RCW 5', 'RCW 9']),
      ];
      final rcw = bookObjects(catalog, AlbumBook.rcw);
      expect(rcw.map((d) => d.id), ['NGC2359', 'RCW 8', 'NGC3372']);
      expect(rcw.map((d) => bookLabel(d, AlbumBook.rcw)), ['RCW 5', 'RCW 8', 'RCW 53']);
      expect(rcw.map((d) => bookNumberOf(d, AlbumBook.rcw)), [5, 8, 53]);
    });

    test("a row's RCW number is worked out once, not again on every build of the Album (#1629)", () {
      // Rows never change, so the scan runs once per row; an alias added afterwards (only a
      // test can) is not seen.
      final aliases = ['Gum 22'];
      final row = _dso('RCW 38', aliases: aliases);
      expect(rcwNumOf(row), 38);
      aliases.add('RCW 1');
      expect(rcwNumOf(row), 38);
    });

    test('an object in two Books is a Card in each, so the RCW count is the true count (#1629)', () {
      final catalog = [_dso('NGC3114', c: 90, aliases: ['RCW 40']), _dso('RCW 41'), _dso('NGC1976', m: 42, aliases: ['RCW 9'])];
      expect(bookObjects(catalog, AlbumBook.messier).map((d) => d.id), ['NGC1976']);
      expect(bookObjects(catalog, AlbumBook.caldwell).map((d) => d.id), ['NGC3114']);
      expect(bookObjects(catalog, AlbumBook.rcw).map((d) => d.id), ['NGC1976', 'NGC3114', 'RCW 41']);
    });

    test('a frame saved as Gum 15 or by NGC number fills the RCW Card (#1629)', () {
      final rcw15 = _dso('RCW 15', aliases: ['Gum 15', 'NGC 2736']);
      for (final name in ['Gum 15', 'Gum15', 'gum 015', 'NGC 2736', 'RCW 15']) {
        final stats = aggregateBook([_frame('a', ra: null, target: name)], [rcw15]);
        expect(stats.keys, ['RCW 15'], reason: name);
      }
      expect(aggregateBook([_frame('a', ra: null, target: 'Gum 16')], [rcw15]), isEmpty);
    });
  });

  test('AlbumFrame parses the snake_case wire row', () {
    final f = AlbumFrame.fromJson({
      'frame_id': 'id1', 'ra_deg': 10.68, 'dec_deg': 41.27, 'width': 4144, 'height': 2822, 'bin': 1,
      'pixel_scale_arcsec': 1.56, 'captured_utc': '2026-03-02T11:20:31Z', 'exposure_seconds': 180,
      'filter_name': 'Ha', 'target_name': 'M31', 'composite_score': 0.83, 'profile_id': 'p',
      'updated_utc': '2026-03-03T00:00:00Z',
    });
    expect((f.frameId, f.raDeg, f.width, f.pixelScaleArcsec, f.exposureSeconds, f.targetName),
        ('id1', 10.68, 4144, 1.56, 180.0, 'M31'));
    expect(f.capturedUtc, DateTime.utc(2026, 3, 2, 11, 20, 31));
    expect(f.updatedUtc, DateTime.utc(2026, 3, 3));
    final back = AlbumFrame.fromJson(f.toJson());
    expect((back.frameId, back.raDeg, back.decDeg, back.width, back.height, back.pixelScaleArcsec, back.capturedUtc,
            back.exposureSeconds, back.filterName, back.targetName, back.updatedUtc),
        (f.frameId, f.raDeg, f.decDeg, f.width, f.height, f.pixelScaleArcsec, f.capturedUtc, f.exposureSeconds,
            f.filterName, f.targetName, f.updatedUtc),
        reason: 'the cache file round-trips the row');
  });

  test(
    'skyPickArt finds the Art by the engine\'s NAME-prefixed common name alone',
    () {
      final catalog = [_dso('NGC1976', name: 'Orion Nebula', m: 42)];
      const art = AlbumArt(asset: 'assets/album/m42.webp');
      expect(
        skyPickArt(
          ['NAME Orion Nebula'],
          'Selected object',
          catalog,
          {'M42': art},
        ),
        same(art),
      );
    },
  );
}
