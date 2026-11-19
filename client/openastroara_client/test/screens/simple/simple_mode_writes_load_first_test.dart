import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/models/equipment_device_status.dart';
import 'package:openastroara/models/focuser_status.dart';
import 'package:openastroara/screens/simple/simple_focus_panel.dart';
import 'package:openastroara/services/profile_api.dart';
import 'package:openastroara/state/equipment/focuser_state.dart';
import 'package:openastroara/state/profile_management_state.dart';
import 'package:openastroara/state/settings/autofocus_settings_state.dart';
import 'package:openastroara/state/simple_messages_state.dart';
import 'package:openastroara/state/simple_mode_state.dart';

import '../../support/fake_section_rig.dart';

// ADR 0015, slice 3 of #1807: a Simple Mode window that saves a section it
// never loaded. The autofocus exposure pick is the one write that shows both
// outcomes in a widget: the section is never loaded by the panel (it relies on
// Settings or live-sync having done it), so the pick must load the rig's copy
// first, refuse when it cannot, and keep the pick when it can. Assertions are
// on the rig's side (FakeSectionRig.puts) and the pill the person sees.
// The device windows and Settings › Rig have their own files next to this one.

const _route = 'autofocus';

/// The rig's autofocus copy: the exposure the pick changes, and two fields it
/// does not (a client default for either would show in the PUT).
final _rigCopy = ProfileApi.autofocusSettingsToJson(
  const AutofocusSettings(exposureSeconds: 7, steps: 9, stepSize: 80),
);

FocuserStatus _focuser() => FocuserStatus(
  deviceId: 'f',
  name: 'Focuser',
  connectionState: EquipmentConnectionState.connected,
  capabilities: FocuserCapabilities(
    minPosition: 0,
    maxPosition: 1100,
    stepSizeUm: 1,
    canTempComp: false,
    absoluteFocuser: true,
  ),
  runtimeState: 'idle',
  position: 1000,
  temperature: null,
  tempCompEnabled: false,
);

class _Focuser extends FocuserNotifier {
  @override
  Future<FocuserStatus?> build() async => _focuser();
}

Future<ProviderContainer> _pump(WidgetTester tester, FakeSectionRig rig) async {
  final c = ProviderContainer(
    overrides: [
      focuserProvider.overrideWith(_Focuser.new),
      profileApiProvider.overrideWithValue(rig.api()),
      simpleModeActiveProvider.overrideWithValue(true),
    ],
  );
  addTearDown(c.dispose);
  tester.view.physicalSize = const Size(800, 1000);
  tester.view.devicePixelRatio = 1;
  addTearDown(tester.view.reset);
  await tester.pumpWidget(
    UncontrolledProviderScope(
      container: c,
      child: const MaterialApp(
        home: Scaffold(body: Center(child: SimpleFocusPanel())),
      ),
    ),
  );
  await tester.pump();
  return c;
}

void main() {
  testWidgets('an autofocus exposure pick with the rig copy unreachable writes '
      "nothing and says it couldn't save", (tester) async {
    final rig = FakeSectionRig({_route: _rigCopy})..failGet.add(_route);
    final c = await _pump(tester, rig);
    await tester.runAsync(() async {
      await tester.tap(find.byKey(const Key('simple-focus-af-exposure-10')));
    });
    await tester.runAsync(
      () => settle(
        () => rig.puts.isNotEmpty || c.read(simpleMessagesProvider).isNotEmpty,
      ),
    );
    await tester.pump();
    expect(rig.puts, isEmpty);
    expect(c.read(simpleMessagesProvider).single.title, "Couldn't save that");
  });

  testWidgets("an autofocus exposure pick keeps the rig's other values and the "
      'pick', (tester) async {
    // Not loaded at the pick: a load the panel fires on its own fails first,
    // so only a load at the pick itself gives the rig's copy.
    final rig = FakeSectionRig({_route: _rigCopy})..failGet.add(_route);
    await _pump(tester, rig);
    await tester.runAsync(
      () => Future<void>.delayed(const Duration(milliseconds: 50)),
    );
    rig.failGet.remove(_route);
    await tester.runAsync(() async {
      await tester.tap(find.byKey(const Key('simple-focus-af-exposure-10')));
    });
    await tester.runAsync(() => settle(() => rig.puts.isNotEmpty));
    await tester.pump();
    expect(rig.puts, hasLength(1));
    expect(rig.puts.single.body, {..._rigCopy, 'exposure_seconds': 10});
  });

  testWidgets('an autofocus exposure pick after another device saved the '
      "section sends the rig's current tag, never a stale one", (tester) async {
    final rig = FakeSectionRig({_route: _rigCopy});
    final c = await _pump(tester, rig);
    await tester.runAsync(
      () => c
          .read(autofocusSettingsProvider.notifier)
          .hydrateFromServer(rig.api()),
    );
    rig.savedElsewhere(_route); // the tag the section was loaded with is old
    final current = rig.etagOf(_route);
    await tester.runAsync(() async {
      await tester.tap(find.byKey(const Key('simple-focus-af-exposure-10')));
    });
    await tester.runAsync(() => settle(() => rig.ifMatches.isNotEmpty));
    await tester.pump();
    // Loaded again before the write: the PUT lands with the current tag.
    expect(rig.ifMatches, [current]);
    expect(rig.puts, hasLength(1));
  });
}
