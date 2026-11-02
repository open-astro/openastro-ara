import 'dart:async';
import 'dart:io';

import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/services/simple_mode_prefs_service.dart';
import 'package:openastroara/state/simple_mode_state.dart';

/// Wraps a real service with a load() the test releases by hand, to open the
/// hydrate-vs-toggle race deterministically.
class _SlowPrefs implements SimpleModePrefsService {
  _SlowPrefs(this._inner);
  final SimpleModePrefsService _inner;
  final _gate = Completer<void>();
  void release() => _gate.complete();

  @override
  Future<bool> load() async {
    await _gate.future;
    return _inner.load();
  }

  @override
  Future<void> save(bool enabled) => _inner.save(enabled);

  @override
  noSuchMethod(Invocation i) => super.noSuchMethod(i);
}

void main() {
  late Directory dir;
  setUp(() => dir = Directory.systemTemp.createTempSync('simple_mode_prefs'));
  tearDown(() => dir.deleteSync(recursive: true));

  SimpleModePrefsService svc() =>
      SimpleModePrefsService(supportDir: () async => dir);

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
    File('${dir.path}/simple_mode.json').writeAsStringSync('not json');
    expect(await svc().load(), isFalse);
  });

  test('a toggle during the initial read is not clobbered by it', () async {
    final prefs = _SlowPrefs(svc());
    final container = ProviderContainer(
      overrides: [simpleModePrefsProvider.overrideWithValue(prefs)],
    );
    addTearDown(container.dispose);

    // Start the (slow) hydrating read, then toggle before it resolves.
    final hydrated = container.read(simpleModeProvider.future);
    await container.read(simpleModeProvider.notifier).set(true);
    prefs.release();
    expect(await hydrated, isTrue);
    expect(container.read(simpleModeProvider).value, isTrue);
  });

  test(
    'the controller hydrates from, and writes back to, the prefs file',
    () async {
      final prefs = svc();
      await prefs.save(true);
      final container = ProviderContainer(
        overrides: [simpleModePrefsProvider.overrideWithValue(prefs)],
      );
      addTearDown(container.dispose);

      expect(await container.read(simpleModeProvider.future), isTrue);
      await container.read(simpleModeProvider.notifier).set(false);
      expect(container.read(simpleModeProvider).value, isFalse);
      expect(await prefs.load(), isFalse);
    },
  );
}
