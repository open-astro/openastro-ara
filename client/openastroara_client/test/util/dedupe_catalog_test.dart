import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/services/bundled_catalogs.dart';
import 'package:openastroara/services/dso_catalog_service.dart';
import 'package:openastroara/util/dedupe_catalog.dart';

PlanningDso _row(
  String id, {
  String? name,
  String type = 'HII',
  double? mag,
  double? sb,
  double ra = 314.70,
  double dec = 44.52,
  double? maj,
  List<String> aliases = const [],
  int? messier,
  int? caldwell,
}) => PlanningDso(
  id: id,
  name: name ?? id,
  type: type,
  magnitude: mag,
  raDeg: ra,
  decDeg: dec,
  sizeMajArcmin: maj,
  surfaceBrightness: sb,
  messierNum: messier,
  caldwellNum: caldwell,
  aliases: aliases,
);

void main() {
  test('designation keys ignore case, spacing, dashes and zero padding', () {
    expect(designationKey('NGC 0224'), 'NGC224');
    expect(designationKey('ngc0224'), 'NGC224');
    expect(designationKey('Sh2-117'), 'SH2-117', reason: 'dashes stay');
    expect(looseDesignationKey('Sh2-117'), 'SH2117');
    expect(designationKey('C 020'), 'C20');
    expect(designationKey('NGC 1000'), 'NGC1000');
  });

  test('families and type classes', () {
    expect(catalogFamily('NGC7000'), 'openngc');
    expect(catalogFamily('IC0434'), 'openngc');
    expect(catalogFamily('Sh2-117'), 'sharpless');
    expect(catalogFamily('B33'), 'barnard');
    expect(catalogFamily('LDN 1235'), 'ldn');
    expect(catalogFamily('vdB 28'), 'vdb');
    expect(catalogFamily('REGION-SH2-101'), 'region');
    expect(typeClass('HII'), typeClass('Neb'));
    expect(typeClass('G'), typeClass('GPair'));
    expect(typeClass('DrkN'), isNot(typeClass('HII')));
    expect(typeClass('WR*'), 'star');
  });

  test(
    'rows sharing a cross-identification fold into one, keyed on the NGC row',
    () {
      // NGC 7000 (Identifiers "C 020,LBN 373"); Sh2-117 12′ away with no
      // cross-ids, linked by the curated twin list; an LBN row that names
      // NGC 7000 as its own alias; and a vdB row 2° away that stays separate.
      final merged = mergeDuplicates(
        links: [('NGC7000', 'Sh2-117')],
        [
          _row('Sh2-117', ra: 314.75, dec: 44.37, maj: 120),
          _row(
            'NGC7000',
            name: 'North America Nebula',
            type: 'Neb',
            sb: 22.0,
            aliases: ['C 020', 'LBN 373'],
            caldwell: 20,
          ),
          _row('vdB 1', type: 'RfN', ra: 316.7, dec: 44.5),
          _row(
            'C 020-like',
            type: 'Neb',
            ra: 10,
            dec: 10,
            aliases: ['NGC 7000'],
          ),
        ],
      );
      expect(merged.map((d) => d.id), ['NGC7000', 'vdB 1']);
      final nam = merged.first;
      expect(nam.name, 'North America Nebula');
      expect(
        nam.surfaceBrightness,
        22.0,
        reason: 'the primary keeps its photometry',
      );
      expect(nam.caldwellNum, 20);
      expect(
        nam.sizeMajArcmin,
        120,
        reason: 'a missing size is taken from a member',
      );
      // ...and the whole extent comes from that one member.
      final ext = mergeDuplicates([
        _row('A', mag: 5, ra: 1, dec: 1, aliases: ['B']),
        const PlanningDso(
          id: 'B',
          name: 'B',
          type: 'HII',
          magnitude: null,
          raDeg: 1,
          decDeg: 1,
          sizeMajArcmin: 10,
          sizeMinArcmin: 4,
          posAngleDeg: 30,
        ),
        const PlanningDso(
          id: 'C',
          name: 'C',
          type: 'HII',
          magnitude: null,
          raDeg: 1,
          decDeg: 1,
          sizeMinArcmin: 99,
          aliases: ['A'],
        ),
      ]).single;
      expect(
        (ext.sizeMajArcmin, ext.sizeMinArcmin, ext.posAngleDeg),
        (10, 4, 30),
      );
      expect(
        nam.aliases,
        containsAll(['C 020', 'LBN 373', 'Sh2-117', 'C 020-like']),
      );
      expect(nam.aliases, isNot(contains('NGC7000')));
      expect(nam.aliases, isNot(contains('North America Nebula')));
    },
  );

  test('position merges only across families and within the same class', () {
    final merged = mergeDuplicates([
      _row('vdB 21', type: 'RfN', ra: 56.1670, dec: 24.1130),
      _row('NGC1432', type: 'HII', mag: 4.2, ra: 56.1668, dec: 24.1131),
      // Two NGC rows at one spot are two objects (a galaxy pair).
      _row('NGC4567', type: 'G', mag: 11.3, ra: 189.08, dec: 11.26),
      _row('NGC4568', type: 'G', mag: 10.8, ra: 189.08, dec: 11.26),
      // A dark nebula on top of an emission nebula is not the same thing.
      _row('LDN 1', type: 'DrkN', ra: 100.0, dec: 10.0),
      _row('Sh2-1', type: 'HII', ra: 100.0, dec: 10.0),
      // Same family, same class, same spot: never merged by position.
      _row('WR 1', type: 'WR*', ra: 200.0, dec: 20.0),
      _row('WR 2', type: 'WR*', ra: 200.0, dec: 20.0),
    ]);
    expect(merged.map((d) => d.id), [
      'NGC1432',
      'NGC4567',
      'NGC4568',
      'LDN 1',
      'Sh2-1',
      'WR 1',
      'WR 2',
    ]);
    expect(merged.first.aliases, ['vdB 21']);
  });

  test('the row with photometry wins, then the NGC/IC designation', () {
    final a = mergeDuplicates([
      _row('Sh2-25', mag: 6.0, ra: 1, dec: 1, aliases: ['NGC 6523']),
      _row('NGC6523', name: 'Lagoon Nebula', ra: 1, dec: 1),
    ]);
    expect(a.single.id, 'Sh2-25', reason: 'the magnitude wins');
    final b = mergeDuplicates([
      _row('Sh2-25', ra: 1, dec: 1, aliases: ['NGC 6523']),
      _row('NGC6523', name: 'Lagoon Nebula', ra: 1, dec: 1),
    ]);
    expect(b.single.id, 'NGC6523');
    expect(b.single.aliases, [
      'Sh2-25',
    ], reason: "the primary's own designation is not an alias");
  });

  test('a row naming several other rows is a group, not an identity', () {
    // Arp 281 lists NGC 4627 and NGC 4631: the pair is the Arp object; the
    // two galaxies are their own rows and keep their own data (review r2).
    final merged = mergeDuplicates([
      _row(
        'NGC4627',
        type: 'G',
        mag: 12.5,
        ra: 190.65,
        dec: 32.57,
        caldwell: null,
      ),
      _row(
        'NGC4631',
        name: 'Whale Galaxy',
        type: 'G',
        mag: 9.2,
        ra: 190.53,
        dec: 32.54,
        caldwell: 32,
      ),
      _row(
        'Arp 281',
        type: 'G',
        ra: 190.60,
        dec: 32.55,
        aliases: ['NGC 4627', 'NGC 4631'],
      ),
      // A row naming ONE other row is still an identity (Arp 1 = NGC 2857).
      _row('Arp 1', type: 'G', ra: 10, dec: 10, aliases: ['NGC 2857']),
      _row('NGC2857', type: 'G', mag: 12.4, ra: 10, dec: 10),
    ]);
    expect(merged.map((d) => d.id), [
      'NGC4627',
      'NGC4631',
      'Arp 281',
      'NGC2857',
    ]);
    expect(merged[1].caldwellNum, 32);
    expect(merged.last.aliases, ['Arp 1']);
  });

  test('a star row never wins a merge and a link to a star is ignored', () {
    // IC 1318 is a star row (gam Cyg, V 2.2) in OpenNGC; the anchor links it
    // to Sh2-108. The nebula must stay a nebula row (review r1).
    final merged = mergeDuplicates(
      links: [('IC1318', 'Sh2-108')],
      [
        _row('IC1318', type: '*', mag: 2.23, ra: 305.56, dec: 40.26),
        _row('Sh2-108', ra: 305.5, dec: 40.5, maj: 120),
      ],
    );
    expect(merged.map((d) => d.id), ['IC1318', 'Sh2-108']);
    // Two star rows that ARE one star (a WR row and its HD identifier) merge,
    // and the one with photometry wins as usual.
    final stars = mergeDuplicates([
      _row('WR 1', type: 'WR*', mag: 10.1, ra: 1, dec: 1, aliases: ['HD 4004']),
      _row('HD 4004', type: '*', ra: 1, dec: 1),
    ]);
    expect(stars.single.id, 'WR 1');
  });

  test('the real twin list links NGC/IC rows to their Sharpless names', () {
    expect(catalogTwinLinks, contains(('NGC7000', 'Sh2-117')));
    expect(catalogTwinLinks.where((l) => l.$1.startsWith('REGION-')), isEmpty);
  });

  test(
    'a dashed designation never resolves to a dashless key, and RA wraps at 0h',
    () {
      final merged = mergeDuplicates([
        _row('WR 2-1', type: 'WR*', ra: 1, dec: 1),
        _row('WR 21', type: 'WR*', ra: 50, dec: 1),
        _row('HD 1', type: '*', ra: 90, dec: 1, aliases: ['WR 21']),
        // 0.2″ either side of 0h: one object.
        _row('vdB 9', type: 'RfN', ra: 359.99995, dec: 10),
        _row('NGC0009', type: 'RfN', ra: 0.00005, dec: 10),
      ]);
      // Dashes stay in the key, so "WR 21" and "WR 2-1" never collide and the
      // HD row that names WR 21 folds into that star, not the other (r3).
      expect(merged.map((d) => d.id), ['WR 2-1', 'WR 21', 'NGC0009']);
      expect(merged[1].aliases, ['HD 1']);
      expect(merged.last.aliases, ['vdB 9']);
    },
  );

  test('a single row and an empty list pass through', () {
    expect(mergeDuplicates(const []), isEmpty);
    final one = [_row('NGC1')];
    expect(mergeDuplicates(one), same(one));
  });

  test('separation is haversine, in arcminutes', () {
    expect(separationArcmin(0, 0, 0.5, 0), closeTo(30, 1e-9));
    expect(separationArcmin(10, 60, 11, 60), closeTo(30.0, 0.1));
  });
}
