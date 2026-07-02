import 'dart:io';

import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/services/planetarium_prefs_service.dart';

void main() {
  late Directory tmp;
  late PlanetariumPrefsService svc;

  setUp(() {
    tmp = Directory.systemTemp.createTempSync('prefs_test');
    svc = PlanetariumPrefsService(supportDir: () async => tmp);
  });

  tearDown(() {
    try {
      tmp.deleteSync(recursive: true);
    } catch (_) {}
  });

  test('load returns empty when nothing stored', () async {
    expect(await svc.load(), isEmpty);
  });

  test('save merges over stored keys instead of overwriting them', () async {
    // The page can only re-post keys it knows about THIS session — a saved-on
    // catalog whose boot re-fetch failed is absent from later posts. Merge
    // semantics must keep it.
    await svc.save({'cat:messier': true, 'dsos': true});
    await svc.save({'dsos': false, 'atmosphere': true}); // no cat:messier here
    final loaded = await svc.load();
    expect(loaded['cat:messier'], isTrue, reason: 'unmentioned key survives');
    expect(loaded['dsos'], isFalse, reason: 'repeated key takes the new value');
    expect(loaded['atmosphere'], isTrue);
  });

  test('explicit off persists', () async {
    await svc.save({'cat:galaxies': true});
    await svc.save({'cat:galaxies': false});
    expect((await svc.load())['cat:galaxies'], isFalse);
  });

  test('rapid concurrent saves keep both deltas', () async {
    await Future.wait([
      svc.save({'a': true}),
      svc.save({'b': true}),
      svc.save({'c': false}),
    ]);
    final loaded = await svc.load();
    expect(loaded, {'a': true, 'b': true, 'c': false});
  });

  test('corrupt file degrades to empty and save recovers', () async {
    File('${tmp.path}/planetarium_display.json').writeAsStringSync('{not json');
    expect(await svc.load(), isEmpty);
    await svc.save({'dsos': true});
    expect((await svc.load())['dsos'], isTrue);
  });
}
