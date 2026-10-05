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

  test('re-adding the same target moves it up instead of duplicating', () async {
    await svc.remember(_t('A', 10, 20));
    await svc.remember(_t('B', 30, -40));
    final list = await svc.remember(_t('A', 10, 20));
    expect(list.map((t) => t.name), ['A', 'B']);
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
}
