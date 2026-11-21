import 'dart:convert';
import 'dart:io';

import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/services/custom_targets_service.dart';

CustomTarget _t(String name, double ra, double dec, {bool jnow = false}) =>
    CustomTarget(
      name: name,
      raDeg: ra,
      decDeg: dec,
      typedAsJNow: jnow,
      savedUtc: DateTime.utc(2026, 10, 4),
    );

void main() {
  late Directory dir;
  late CustomTargetsService svc;

  setUp(() async {
    dir = await Directory.systemTemp.createTemp('custom-targets');
    svc = CustomTargetsService(supportDir: () async => dir);
  });
  tearDown(() => dir.delete(recursive: true));

  test('writes go through a temp file renamed into place', () async {
    // A torn temp file from an earlier crash is overwritten, never read.
    await File('${dir.path}/custom_targets.json.part').writeAsString('[{');
    await svc.remember(_t('A', 10, 20));
    await svc.remember(_t('B', 30, 40));
    await svc.forget(_t('A', 10, 20));
    final names = dir.listSync().map((e) => e.uri.pathSegments.last).toList();
    expect(names, ['custom_targets.json']);
    expect((await svc.load()).map((t) => t.name), ['B']);
  });

  test('loads empty when nothing is stored', () async {
    expect(await svc.load(), isEmpty);
  });

  test('remember puts the newest first and survives a reload', () async {
    await svc.remember(_t('A', 10, 20));
    final list = await svc.remember(_t('B', 30, -40, jnow: true));
    expect(list.map((t) => t.name), ['B', 'A']);
    final again = await CustomTargetsService(supportDir: () async => dir).load();
    expect(again.map((t) => t.name), ['B', 'A']);
    expect(again.first.typedAsJNow, isTrue);
    expect(again.first.decDeg, -40);
  });

  test('re-adding the same name moves it up instead of duplicating', () async {
    await svc.remember(_t('A', 10, 20));
    await svc.remember(_t('B', 30, -40));
    // Same name, a hair different position (a JNow entry converted again).
    final list = await svc.remember(_t('a ', 10.00001, 20));
    expect(list.map((t) => t.name), ['a ', 'B']);
    expect(list.first.raDeg, 10.00001);
  });

  test('caps the list', () async {
    for (var i = 0; i < CustomTargetsService.maxEntries + 5; i++) {
      await svc.remember(_t('T$i', i.toDouble(), 0));
    }
    final list = await svc.load();
    expect(list.length, CustomTargetsService.maxEntries);
    expect(list.first.name, 'T${CustomTargetsService.maxEntries + 4}');
  });

  test('forget removes one entry', () async {
    await svc.remember(_t('A', 10, 20));
    await svc.remember(_t('B', 30, -40));
    final list = await svc.forget(_t('A', 10, 20));
    expect(list.map((t) => t.name), ['B']);
  });

  test('a corrupt file degrades to empty and bad rows are dropped', () async {
    final f = File('${dir.path}/custom_targets.json');
    await f.writeAsString('not json');
    expect(await svc.load(), isEmpty);
    await f.writeAsString(
        '[{"name":"ok","ra_deg":1,"dec_deg":2},{"name":"bad","ra_deg":1,'
        '"dec_deg":95},{"ra_deg":1,"dec_deg":2},"x"]');
    final list = await svc.load();
    expect(list.map((t) => t.name), ['ok']);
  });

  test('aliases and source round-trip through the mirror and the wire shape (#1267 item 5)', () async {
    final resolved = CustomTarget(
      name: 'AB Cas',
      raDeg: 39.38,
      decDeg: 71.30,
      typedAsJNow: false,
      savedUtc: DateTime.utc(2026, 10, 18, 22),
      aliases: const ['HIP 12235', 'TYC 4320-505-1'],
      source: 'simbad',
    );
    await svc.remember(resolved);
    final back = (await svc.load()).single;
    expect(back.aliases, ['HIP 12235', 'TYC 4320-505-1']);
    expect(back.source, 'simbad');
    expect(CustomTarget.fromJson(_t('x', 1, 2).toJson())!.source, 'typed');
    expect(CustomTarget.fromJson(_t('x', 1, 2).toJson())!.aliases, isEmpty);
    final wire = resolved.toWire();
    expect(wire['typed_as_j_now'], false);
    expect(wire['source'], 'simbad');
    final fromWire = CustomTarget.fromWire({...wire, 'typed_as_j_now': true})!;
    expect(fromWire.typedAsJNow, isTrue);
    expect(fromWire.aliases, resolved.aliases);
    final replaced = await svc.replaceAll([_t('b', 1, 1), _t('a', 2, 2)]);
    expect(replaced.map((t) => t.name), ['b', 'a']);
    expect((await svc.load()).map((t) => t.name), ['b', 'a']);
  });

  test('scopes keep rigs apart, the last scope answers offline, a legacy list is adopted (#1267 item 5)', () async {
    // A pre-#1267 device-wide list on disk.
    final f = File('${dir.path}/custom_targets.json');
    await f.writeAsString(jsonEncode([_t('Old field', 1, 2).toJson()]));
    final legacy = await svc.loadScope('http://a|p1');
    expect(legacy.targets.map((t) => t.name), ['Old field'], reason: 'adopted by the first scope');
    expect(legacy.base, isEmpty);
    await svc.remember(_t('A1', 1, 1), scope: 'http://a|p1');
    await svc.remember(_t('B1', 2, 2), scope: 'http://b|p9');
    expect((await svc.load(scope: 'http://a|p1')).map((t) => t.name), ['A1', 'Old field']);
    expect((await svc.load(scope: 'http://b|p9')).map((t) => t.name), ['B1']);
    expect((await svc.loadScope(null)).targets.map((t) => t.name), ['B1'], reason: 'offline: the scope used last');
    // commitSync records the profile's names as the base; forget keeps it.
    await svc.commitSync('http://a|p1', [_t('A1', 1, 1), _t('A2', 3, 3)]);
    final synced = await svc.loadScope('http://a|p1');
    expect(synced.targets.map((t) => t.name), ['A1', 'A2']);
    expect(synced.base, {'a1', 'a2'});
    await svc.forget(_t('A2', 3, 3), scope: 'http://a|p1');
    final after = await svc.loadScope('http://a|p1');
    expect(after.targets.map((t) => t.name), ['A1']);
    expect(after.base, {'a1', 'a2'}, reason: 'the base is what the profile held at the last sync');
    await svc.remember(_t('A2', 4, 4), scope: 'http://a|p1');
    expect((await svc.loadScope('http://a|p1')).base, {'a1'}, reason: 'a re-add is a fresh local add');
    // Targets saved before this device had a server and profile live in
    // 'local' and are adopted by the first real scope.
    final freshDir = await Directory.systemTemp.createTemp('ct-local');
    addTearDown(() => freshDir.delete(recursive: true));
    final fresh = CustomTargetsService(supportDir: () async => freshDir);
    await fresh.remember(_t('Offline first', 1, 1));
    expect((await fresh.loadScope(null)).targets.map((t) => t.name), ['Offline first']);
    expect((await fresh.loadScope('http://c|p1')).targets.map((t) => t.name), ['Offline first']);
    await fresh.commitSync('http://c|p1', [_t('Offline first', 1, 1)]);
    expect((await fresh.loadScope('http://d|p2')).targets, isEmpty, reason: 'adopted once, then gone');
  });

  test('isAcceptableSavedTarget mirrors the daemon rules', () {
    expect(isAcceptableSavedTarget(_t('ok', 1, 2)), isTrue);
    expect(isAcceptableSavedTarget(_t('  ', 1, 2)), isFalse);
    expect(isAcceptableSavedTarget(_t('a\nb', 1, 2)), isFalse);
    expect(isAcceptableSavedTarget(_t('a\u0085b', 1, 2)), isFalse, reason: 'C1 controls, like the daemon');
    expect(isAcceptableSavedTarget(_t('Ünïcode', 1, 2)), isTrue);
    expect(isAcceptableSavedTarget(_t('x' * 129, 1, 2)), isFalse);
    expect(isAcceptableSavedTarget(_t('ok', 1, 91)), isFalse);
    expect(isAcceptableSavedTarget(_t('ok', double.nan, 2)), isFalse);
    expect(
      isAcceptableSavedTarget(CustomTarget(name: 'ok', raDeg: 1, decDeg: 2, typedAsJNow: false, savedUtc: DateTime.utc(2026), source: 'gaia')),
      isFalse,
    );
  });
}
