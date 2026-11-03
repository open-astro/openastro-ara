import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/services/dso_catalog_service.dart';

PlanningDso _o(String id, String name, double? mag, {List<String> aliases = const []}) => PlanningDso(
    id: id, name: name, type: 'G', magnitude: mag, raDeg: 0, decDeg: 0, aliases: aliases);

final _catalog = [
  _o('NGC0224', 'Andromeda Galaxy', 3.4, aliases: ['M 31']),
  _o('NGC0598', 'Triangulum Galaxy', 5.7, aliases: ['M 33']),
  _o('NGC5272', '', 6.2, aliases: ['M 3']),
  _o('NGC7000', 'North America Nebula', 4, aliases: ['Sh2-117', 'C 20']),
  _o('Sh2-129', 'Flying Bat Nebula', null),
  _o('NGC1976', 'Orion Nebula', 4, aliases: ['M 42']),
];

void main() {
  test('suggestions start at two characters', () {
    expect(searchSuggestions(_catalog, 'm'), isEmpty);
    expect(searchSuggestions(_catalog, ' '), isEmpty);
  });

  test('an exact designation comes first, then designations that start with what was typed', () {
    final s = searchSuggestions(_catalog, 'm3');
    expect(s.first.label, 'M 3', reason: 'exact');
    expect(s.map((e) => e.label), containsAll(['M 31', 'M 33']));
    expect(s.map((e) => e.label), isNot(contains('M 42')));
  });

  test('spacing and case don\'t matter; a typed dash still matches only dashed designations', () {
    expect(searchSuggestions(_catalog, 'm31').first.label, 'M 31');
    expect(searchSuggestions(_catalog, 'sh2 1').map((e) => e.label), containsAll(['Sh2-117', 'Sh2-129']));
  });

  test('common names match by any word\'s start', () {
    expect(searchSuggestions(_catalog, 'andro').single.dso.id, 'NGC0224');
    expect(searchSuggestions(_catalog, 'nebula').map((e) => e.dso.id), containsAll(['NGC7000', 'Sh2-129', 'NGC1976']));
  });

  test('at most [limit] suggestions, brighter first within a rank', () {
    final s = searchSuggestions(_catalog, 'nebula', limit: 2);
    expect(s, hasLength(2));
    expect(s.first.dso.magnitude, lessThanOrEqualTo(s.last.dso.magnitude ?? 99));
  });
}
