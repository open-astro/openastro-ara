import 'dart:io';

import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/services/night_mode_prefs_service.dart';
import 'package:openastroara/state/night_mode_state.dart';

void main() {
  late Directory dir;
  setUp(() => dir = Directory.systemTemp.createTempSync('night_mode_prefs'));
  tearDown(() => dir.deleteSync(recursive: true));

  NightModePrefsService svc() =>
      NightModePrefsService(supportDir: () async => dir);

  test('defaults to off when nothing is stored', () async {
    expect(await svc().load(), isFalse);
  });

  test('a saved flag survives a reload', () async {
    await svc().save(true);
    expect(await svc().load(), isTrue);
    await svc().save(false);
    expect(await svc().load(), isFalse);
  });

  test('a corrupt prefs file degrades to off instead of throwing', () async {
    File('${dir.path}/night_mode.json').writeAsStringSync('not json');
    expect(await svc().load(), isFalse);
  });

  test('the controller hydrates from, and writes back to, the prefs file',
      () async {
    final prefs = svc();
    await prefs.save(true);
    final container = ProviderContainer(
      overrides: [nightModePrefsProvider.overrideWithValue(prefs)],
    );
    addTearDown(container.dispose);

    expect(await container.read(nightModeProvider.future), isTrue);
    await container.read(nightModeProvider.notifier).toggle();
    expect(container.read(nightModeProvider).value, isFalse);
    expect(await prefs.load(), isFalse);
  });
}
