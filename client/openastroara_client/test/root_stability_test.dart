import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/main.dart';
import 'package:openastroara/services/client_gps_prefs_service.dart';
import 'package:openastroara/services/night_mode_prefs_service.dart';
import 'package:openastroara/state/client_gps_state.dart';
import 'package:openastroara/state/night_mode_state.dart';
import 'package:openastroara/theme/ara_theme.dart';
import 'package:openastroara/widgets/night_filter.dart';
import 'package:openastroara/widgets/night_hotkey.dart';

/// #1111 — the app root must hold still. Every rebuild of `OpenAstroAraApp`
/// rebuilds MaterialApp → WidgetsApp → Navigator, and a night-mode toggle
/// used to reparent the Navigator outright. These tests pin the identities
/// that must survive the two runtime signals that reach the root.
class _MemPrefs extends NightModePrefsService {
  bool value = false;
  @override
  Future<bool> load() async => value;
  @override
  Future<void> save(bool enabled) async => value = enabled;
}

/// Emits fresh status objects the way a real sync does (busy on, busy off).
class _FakeGps extends ClientGpsNotifier {
  @override
  Future<ClientGpsStatus> build() async =>
      const ClientGpsStatus(prefs: ClientGpsPrefs(), supported: false);

  void emit({required bool busy}) {
    state = AsyncData(
      ClientGpsStatus(prefs: const ClientGpsPrefs(), supported: false, busy: busy),
    );
  }
}

void main() {
  late ProviderContainer container;

  Future<void> pump(WidgetTester tester) async {
    container = ProviderContainer(overrides: [
      nightModePrefsProvider.overrideWithValue(_MemPrefs()),
      clientGpsProvider.overrideWith(_FakeGps.new),
    ]);
    addTearDown(container.dispose);
    await tester.pumpWidget(UncontrolledProviderScope(
      container: container,
      child: const OpenAstroAraApp(home: Scaffold(body: Text('root'))),
    ));
    await container.read(nightModeProvider.future);
    await container.read(clientGpsProvider.future);
    await tester.pump();
  }

  ({MaterialApp app, NavigatorState nav, ThemeData theme, Element hotkeyFocus,
    RenderNightFilter filter}) snapshot(WidgetTester tester) {
    final rootContext = tester.element(find.text('root'));
    return (
      app: tester.widget<MaterialApp>(find.byType(MaterialApp)),
      nav: Navigator.of(rootContext),
      theme: Theme.of(rootContext),
      hotkeyFocus: tester.element(find.descendant(
        of: find.byType(NightHotkey),
        matching: find.byType(Focus),
      ).first),
      filter: tester.renderObject<RenderNightFilter>(
        find.byType(NightFilter),
      ),
    );
  }

  testWidgets('the theme is one shared instance', (tester) async {
    await pump(tester);
    final s = snapshot(tester);
    expect(s.app.theme, same(araTheme));
    // Theme.of hands out a localized copy, so it is compared before/after a
    // signal in the tests below rather than against the shared instance.
  });

  testWidgets('GPS status emissions leave the root untouched', (tester) async {
    await pump(tester);
    final before = snapshot(tester);

    final gps = container.read(clientGpsProvider.notifier) as _FakeGps;
    gps.emit(busy: true);
    await tester.pump();
    gps.emit(busy: false);
    await tester.pump();

    final after = snapshot(tester);
    expect(after.app, same(before.app), reason: 'MaterialApp widget rebuilt');
    expect(after.nav, same(before.nav), reason: 'Navigator state replaced');
    expect(after.theme, same(before.theme), reason: 'ThemeData replaced');
    expect(after.hotkeyFocus, same(before.hotkeyFocus));
  });

  testWidgets('a night-mode toggle flips the filter without moving the Navigator',
      (tester) async {
    await pump(tester);
    final before = snapshot(tester);
    expect(before.filter.enabled, isFalse);

    await container.read(nightModeProvider.notifier).toggle();
    await tester.pump();

    final on = snapshot(tester);
    expect(on.filter, same(before.filter), reason: 'render object replaced');
    expect(on.filter.enabled, isTrue);
    expect(on.nav, same(before.nav), reason: 'Navigator state replaced');
    expect(on.hotkeyFocus, same(before.hotkeyFocus), reason: 'hotkey Focus remounted');
    expect(on.app, same(before.app));
    expect(find.text('root'), findsOneWidget);

    await container.read(nightModeProvider.notifier).toggle();
    await tester.pump();
    final off = snapshot(tester);
    expect(off.filter.enabled, isFalse);
    expect(off.nav, same(before.nav));
  });

  testWidgets('the night filter paints (on and off) without throwing',
      (tester) async {
    await pump(tester);
    await container.read(nightModeProvider.notifier).set(true);
    await tester.pump();
    expect(tester.takeException(), isNull);
    await container.read(nightModeProvider.notifier).set(false);
    await tester.pump();
    expect(tester.takeException(), isNull);
  });
}
