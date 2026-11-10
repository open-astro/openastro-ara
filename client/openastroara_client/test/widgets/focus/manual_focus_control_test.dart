import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/l10n/app_localizations.dart';
import 'package:openastroara/models/equipment_device_status.dart';
import 'package:openastroara/models/focuser_status.dart';
import 'package:openastroara/state/equipment/focuser_state.dart';
import 'package:openastroara/state/focus/bahtinov_focus_state.dart';
import 'package:openastroara/widgets/focus/focusing_pane.dart';

class _Focuser extends FocuserNotifier {
  final moves = <int>[];
  int halts = 0;
  @override
  Future<FocuserStatus?> build() async => null;
  @override
  Future<bool> move(int targetPosition, {bool useTempComp = false}) async {
    moves.add(targetPosition);
    return true;
  }

  @override
  Future<bool> halt() async {
    halts++;
    return true;
  }
}

FocuserStatus _status({int? position = 1000, String runtime = 'idle', int? max = 2000}) => FocuserStatus(
      deviceId: 'eaf',
      name: 'EAF',
      connectionState: EquipmentConnectionState.connected,
      capabilities: max == null ? null : FocuserCapabilities(minPosition: 0, maxPosition: max, stepSizeUm: 1, canTempComp: false, absoluteFocuser: true),
      runtimeState: runtime,
      position: position,
      temperature: null,
      tempCompEnabled: false,
    );

Future<_Focuser> _pump(WidgetTester tester, FocuserStatus status) async {
  final f = _Focuser();
  await tester.pumpWidget(ProviderScope(
    overrides: [focuserProvider.overrideWith(() => f)],
    child: MaterialApp(
      localizationsDelegates: AppLocalizations.localizationsDelegates,
      supportedLocales: AppLocalizations.supportedLocales,
      home: Scaffold(body: ManualFocusControl(focuser: status)),
    ),
  ));
  await tester.pump();
  return f;
}

final _inward = find.byKey(const Key('manual-focus-in'));
final _outward = find.byKey(const Key('manual-focus-out'));
final _stop = find.byKey(const Key('manual-focus-stop'));

class _Bahtinov extends MainFocusMethodNotifier {
  @override
  MainFocusMethod? build() => MainFocusMethod.bahtinov;
}

class _Connected extends FocuserNotifier {
  @override
  Future<FocuserStatus?> build() async => _status();
}

void main() {
  testWidgets('the Bahtinov card has the nudge too (#1542)', (tester) async {
    tester.view.physicalSize = const Size(2400, 3600);
    addTearDown(tester.view.resetPhysicalSize);
    await tester.pumpWidget(ProviderScope(
      overrides: [
        focuserProvider.overrideWith(_Connected.new),
        mainFocusMethodProvider.overrideWith(_Bahtinov.new),
      ],
      child: MaterialApp(
        localizationsDelegates: AppLocalizations.localizationsDelegates,
        supportedLocales: AppLocalizations.supportedLocales,
        home: const Scaffold(body: SizedBox(width: 1100, height: 1600, child: MainFocusCard())),
      ),
    ));
    await tester.pump();
    expect(_stop, findsOneWidget);
  });

  testWidgets('the step choices are named for a screen reader', (tester) async {
    await _pump(tester, _status());
    expect(find.byTooltip('200 steps'), findsOneWidget);
  });

  testWidgets('the arrows nudge by the chosen step (#1542)', (tester) async {
    final f = await _pump(tester, _status());
    await tester.tap(_inward);
    await tester.tap(_outward);
    await tester.tap(find.text('200'));
    await tester.pump();
    await tester.tap(_outward);
    expect(f.moves, [950, 1050, 1200]);
  });

  testWidgets('a nudge stops at the inner end of travel', (tester) async {
    final f = await _pump(tester, _status(position: 20));
    await tester.tap(_inward);
    expect(f.moves, [0]);
  });

  testWidgets('a nudge stops at the outer end of travel', (tester) async {
    final f = await _pump(tester, _status(position: 1990));
    await tester.tap(_outward);
    expect(f.moves, [2000]);
  });

  testWidgets('while it moves only Stop works, and Stop halts', (tester) async {
    final f = await _pump(tester, _status(runtime: 'moving'));
    await tester.tap(_inward, warnIfMissed: false);
    expect(f.moves, isEmpty);
    await tester.tap(_stop);
    expect(f.halts, 1);
  });

  testWidgets('idle, Stop has nothing to stop', (tester) async {
    final f = await _pump(tester, _status());
    await tester.tap(_stop, warnIfMissed: false);
    expect(f.halts, 0);
  });

  testWidgets('an unknown position cannot be nudged', (tester) async {
    final f = await _pump(tester, _status(position: null));
    await tester.tap(_outward, warnIfMissed: false);
    expect(f.moves, isEmpty);
  });
}
