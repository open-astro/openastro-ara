import 'dart:async';

import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_riverpod/misc.dart' show Override;
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/models/server.dart';
import 'package:openastroara/screens/simple/simple_shell.dart';
import 'package:openastroara/services/profile_api.dart';
import 'package:openastroara/services/simple_mode_prefs_service.dart';
import 'package:openastroara/state/app_shell_state.dart';
import 'package:openastroara/state/profile_management_state.dart';
import 'package:openastroara/state/rig_setup_state.dart';
import 'package:openastroara/state/server_state.dart'
    show discoveredServersProvider;
import 'package:openastroara/state/settings/optics_settings_state.dart';
import 'package:openastroara/state/settings/settings_nav.dart';
import 'package:openastroara/state/simple_messages_state.dart';
import 'package:openastroara/state/simple_mode_state.dart';
import 'package:openastroara/state/sky_atlas/planetarium_selection_state.dart';

import '../../support/fake_section_rig.dart';

// ADR 0015, slice 3 of #1807: Simple Mode's Settings › Rig page saves the
// optics, guider and site sections it never loaded. Each write must load the
// rig's copy first, refuse (no PUT, the "Couldn't save" pill) when it cannot,
// and keep the rig's other values with the edit when it can. Assertions are
// on the rig's side (FakeSectionRig.puts) and the pill the person sees, so
// they hold whatever shape the notifiers take.

/// The rig's optics: the focal length the row changes and fields it does not.
final _optics = ProfileApi.opticsToJson(
  const OpticsSettings(
    apertureMm: 150,
    focalLengthMm: 1000,
    reducerFactor: 0.8,
    sensorWidthPx: 6248,
    sensorHeightPx: 4176,
    pixelSizeUm: 3.76,
    telescopeName: 'Made-up Refractor',
  ),
);

const _couldntSave = "Couldn't save that";

class _MemPrefs extends SimpleModePrefsService {
  _MemPrefs(this.value);
  final bool value;
  @override
  Future<bool> load() async => value;
  @override
  Future<void> save(bool enabled) async {}
}

class _StartOnLive extends SelectedTabIndexNotifier {
  @override
  int build() => kLiveTabIndex;
}

class _SkyReady extends PlanetariumReadyNotifier {
  @override
  bool build() => true;
}

Future<ProviderContainer> _pump(
  WidgetTester tester,
  FakeSectionRig rig, {
  List<Override> gear = const [],
}) async {
  tester.view.physicalSize = const Size(1024, 768);
  tester.view.devicePixelRatio = 1;
  addTearDown(tester.view.reset);
  final c = ProviderContainer(
    // A provider the rig answers 404 would retry on the test's clock.
    retry: (_, _) => null,
    overrides: [
      companionPlatformProvider.overrideWithValue(TargetPlatform.linux),
      simpleModePrefsProvider.overrideWithValue(_MemPrefs(true)),
      // The Experimental switch on: Simple Mode is active, so pills show.
      simpleModeFeaturePrefsProvider.overrideWithValue(_MemPrefs(true)),
      // Rig's permanent pier switch reads a file, then loads the site.
      simplePermanentPierPrefsProvider.overrideWithValue(_MemPrefs(false)),
      discoveredServersProvider.overrideWith(
        (ref) => const Stream<AraServer>.empty(),
      ),
      rigSetupHydratedProvider.overrideWith((ref) async => false),
      selectedTabIndexProvider.overrideWith(_StartOnLive.new),
      planetariumReadyProvider.overrideWith(_SkyReady.new),
      profileApiProvider.overrideWithValue(rig.api()),
      ...gear,
    ],
  );
  addTearDown(c.dispose);
  await c.read(simpleModeProvider.future);
  await c.read(simpleModeFeatureProvider.future);
  await tester.pumpWidget(
    UncontrolledProviderScope(
      container: c,
      child: const MaterialApp(
        supportedLocales: [Locale('en', 'US')],
        home: SimpleShell(planetarium: SizedBox(key: Key('sky'))),
      ),
    ),
  );
  await flushLoads(tester);
  return c;
}

/// Settings › Rig, and any read it starts given time to land or fail.
Future<void> _openRig(WidgetTester tester) async {
  await tester.tap(find.byTooltip('Settings'));
  await tester.pump();
  await tester.tap(find.byKey(const Key('simple-menu-nav-Rig')));
  await _settle(tester, () => false, rounds: 10);
}

/// Wait until [done] (at most 200 rounds), turning both the test's clock and
/// the real event loop: the reads and writes go through a client that answers
/// on later turns, some of its steps on the test's clock.
Future<void> _settle(
  WidgetTester tester,
  bool Function() done, {
  int rounds = 200,
}) async {
  for (var i = 0; i < rounds && !done(); i++) {
    await tester.pump(const Duration(milliseconds: 10));
    await tester.runAsync(
      () => Future<void>.delayed(const Duration(milliseconds: 5)),
    );
  }
  await flushLoads(tester);
}

Future<void> _roll(WidgetTester tester, String wheel, int items) async {
  await tester.ensureVisible(find.byKey(Key(wheel)));
  await tester.pumpAndSettle();
  await tester.drag(find.byKey(Key(wheel)), Offset(0, -44.0 * items));
  await tester.pumpAndSettle();
}

Future<void> _openRow(WidgetTester tester, String id) async {
  await tester.ensureVisible(find.byKey(Key('simple-rig-$id')));
  await tester.pumpAndSettle();
  await tester.tap(find.byKey(Key('simple-rig-$id')));
  await tester.pumpAndSettle();
}

/// One Rig write site: the section it saves, how a person makes the edit,
/// and the keys the edit sets.
typedef _Site = ({
  String row,
  String route,
  Map<String, Object?> rigCopy,
  Map<String, Object?> edit,
  List<String> pills,
  Future<void> Function(WidgetTester tester) act,
});

final _sites = <_Site>[
  (
    // The focal length row, not the aperture one: both save at the same
    // site, and a not-loaded focal length starts at 0 with or without a
    // client default (the aperture wheel would start at the default's 80).
    row: 'focal length',
    route: 'optics',
    rigCopy: _optics,
    // Not loaded: 0000 mm; the hundreds wheel one up is 100.
    edit: {'focal_length_mm': 100},
    pills: [_couldntSave],
    act: (tester) async {
      await _openRow(tester, 'focal');
      await _roll(tester, 'simple-rig-focal-wheel-1', 1);
    },
  ),
];

List<String> _titles(ProviderContainer c) => [
  for (final m in c.read(simpleMessagesProvider)) m.title,
];

void main() {
  for (final s in _sites) {
    testWidgets('Rig ${s.row}: with the section not loaded and the rig '
        "unreachable it writes nothing and says it couldn't save", (
      tester,
    ) async {
      final rig = FakeSectionRig({s.route: s.rigCopy})..failGet.add(s.route);
      final c = await _pump(tester, rig);
      await _openRig(tester);
      await s.act(tester);
      await _settle(
        tester,
        () => rig.puts.isNotEmpty || _titles(c).any(s.pills.contains),
      );
      expect(rig.puts, isEmpty);
      expect(_titles(c), anyElement(isIn(s.pills)));
    });

    testWidgets("Rig ${s.row}: keeps the rig's other values and the edit", (
      tester,
    ) async {
      final rig = FakeSectionRig({s.route: s.rigCopy})..failGet.add(s.route);
      await _pump(tester, rig);
      await _openRig(tester);
      // Not loaded at the edit (any read so far failed); the rig answers now.
      rig.failGet.remove(s.route);
      await s.act(tester);
      await _settle(tester, () => rig.puts.isNotEmpty);
      expect(rig.puts, hasLength(1));
      expect(rig.puts.single.body, {...s.rigCopy, ...s.edit});
    });
  }

  testWidgets('Rig focal length: a second wheel settling while the first is '
      'still saving lands too, with no conflict', (tester) async {
    final rig = FakeSectionRig({'optics': _optics});
    final c = await _pump(tester, rig);
    await _openRig(tester);
    await _openRow(tester, 'focal');
    final hold = rig.holdPut['optics'] = Completer<void>();
    // 1000 mm: the hundreds wheel one up is 1100, then the tens one 1110.
    await _roll(tester, 'simple-rig-focal-wheel-1', 1);
    await _settle(tester, () => rig.holdPut.isEmpty);
    await _roll(tester, 'simple-rig-focal-wheel-2', 1);
    await _settle(tester, () => false, rounds: 10);
    hold.complete();
    await _settle(
      tester,
      () => rig.puts.length == 2 || _titles(c).contains(_couldntSave),
    );
    expect(_titles(c), isNot(contains(_couldntSave)));
    expect(
      [for (final p in rig.puts) (p.body as Map)['focal_length_mm']],
      [1100, 1110],
    );
    expect(c.read(opticsSettingsProvider).conflict, isFalse);
  });
}
