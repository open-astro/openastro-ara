import 'dart:io';

import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/services/advanced_settings_prefs_service.dart';
import 'package:openastroara/state/settings/advanced_settings_state.dart';

void main() {
  test('the Advanced toggle is off to start, and a choice survives a relaunch (#1544)', () async {
    final dir = await Directory.systemTemp.createTemp('advanced_prefs');
    addTearDown(() => dir.delete(recursive: true));
    ProviderContainer launch() {
      final c = ProviderContainer(
        overrides: [
          advancedSettingsPrefsProvider.overrideWithValue(
            AdvancedSettingsPrefsService(supportDir: () async => dir),
          ),
        ],
      );
      addTearDown(c.dispose);
      return c;
    }

    final first = launch();
    expect(await first.read(advancedSettingsProvider.future), isFalse);
    await first.read(advancedSettingsProvider.notifier).set(true);
    expect(first.read(advancedSettingsProvider).value, isTrue);

    final second = launch();
    expect(
      await second.read(advancedSettingsProvider.future),
      isTrue,
      reason: 'read from the file',
    );
    await second.read(advancedSettingsProvider.notifier).set(false);
    expect(await launch().read(advancedSettingsProvider.future), isFalse);
  });

  test('a corrupt prefs file reads as off', () async {
    final dir = await Directory.systemTemp.createTemp('advanced_prefs');
    addTearDown(() => dir.delete(recursive: true));
    File('${dir.path}/advanced_settings.json').writeAsStringSync('not json');
    expect(
      await AdvancedSettingsPrefsService(supportDir: () async => dir).load(),
      isFalse,
    );
  });
}
