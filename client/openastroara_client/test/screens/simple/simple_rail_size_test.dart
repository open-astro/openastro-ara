import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_riverpod/misc.dart' show Override;
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/models/calibration_status.dart';
import 'package:openastroara/models/guider_status.dart';
import 'package:openastroara/models/server.dart';
import 'package:openastroara/screens/simple/simple_calibration.dart'
    show
        SimpleCalibrationTab,
        SimpleCalibrationTabNotifier,
        simpleCalibrationTabProvider;
import 'package:openastroara/screens/simple/simple_shell.dart';
import 'package:openastroara/services/guider_api.dart' show GuiderClient;
import 'package:openastroara/services/simple_mode_prefs_service.dart';
import 'package:openastroara/state/app_shell_state.dart';
import 'package:openastroara/state/guider/guider_calibration_state.dart';
import 'package:openastroara/state/guider/guider_state.dart';
import 'package:openastroara/state/server_state.dart'
    show discoveredServersProvider;
import 'package:openastroara/state/settings/settings_nav.dart'
    show kLiveTabIndex;
import 'package:openastroara/state/simple_mode_state.dart';

// #2038: the right rail keeps one size on every mode. Calibration's shutter
// adds a caption when it has a job, and the rail (content-sized, centred)
// grew and moved with it.

class _MemPrefs extends SimpleModePrefsService {
  @override
  Future<bool> load() async => true;
  @override
  Future<void> save(bool enabled) async {}
}

class _StartOnLive extends SelectedTabIndexNotifier {
  @override
  int build() => kLiveTabIndex;
}

class _GuideTab extends SimpleCalibrationTabNotifier {
  @override
  SimpleCalibrationTab build() => SimpleCalibrationTab.guide;
}

class _Guider extends GuiderStatusNotifier {
  @override
  Future<void> refresh([GuiderClient? client]) async {}
  @override
  Future<GuiderStatus?> build() async => GuiderStatus.fromJson({
    'device_id': 'g',
    'name': 'PHD2',
    'state': 'connected',
    'runtime': {'state': 'stopped'},
  });
}

class _GuiderCal extends GuiderCalibrationNotifier {
  @override
  Future<CalibrationStatusResponse?> build() async =>
      const CalibrationStatusResponse(
        connected: true,
        status: CalibrationStatus(
          darkLibraryExists: false,
          defectMapExists: false,
          darkLibraryLoaded: false,
          defectMapLoaded: false,
        ),
      );
  @override
  Future<void> refresh() async {}
}

Future<void> _pump(WidgetTester tester, List<Override> extra) async {
  tester.view.physicalSize = const Size(1280, 720);
  tester.view.devicePixelRatio = 1;
  addTearDown(tester.view.reset);
  final container = ProviderContainer(
    overrides: [
      companionPlatformProvider.overrideWithValue(TargetPlatform.linux),
      simpleModePrefsProvider.overrideWithValue(_MemPrefs()),
      discoveredServersProvider.overrideWith(
        (ref) => const Stream<AraServer>.empty(),
      ),
      selectedTabIndexProvider.overrideWith(_StartOnLive.new),
      ...extra,
    ],
  );
  addTearDown(container.dispose);
  await container.read(simpleModeProvider.future);
  await tester.pumpWidget(
    UncontrolledProviderScope(
      container: container,
      child: const MaterialApp(
        home: SimpleShell(planetarium: SizedBox(key: Key('sky'))),
      ),
    ),
  );
  await tester.pump();
}

Rect _rail(WidgetTester tester) =>
    tester.getRect(find.byKey(const Key('simple-rail-tabs')));

Future<void> _open(WidgetTester tester, String slug) async {
  final tab = find.byKey(Key('simple-rail-tab-$slug'));
  await tester.ensureVisible(tab);
  await tester.tap(tab);
  await tester.pumpAndSettle();
}

void main() {
  for (final (name, ready) in const [
    ('idle', false),
    ('with a job ready', true),
  ]) {
    testWidgets('the rail is the same size on Preview and Calibration, $name '
        '(#2038)', (tester) async {
      await _pump(tester, [
        if (ready) ...[
          guiderStatusProvider.overrideWith(_Guider.new),
          guiderCalibrationProvider.overrideWith(_GuiderCal.new),
          simpleCalibrationTabProvider.overrideWith(_GuideTab.new),
        ],
      ]);
      await _open(tester, 'preview');
      final preview = _rail(tester);
      await _open(tester, 'calibration');
      expect(
        find.byKey(const Key('simple-calibration-shutter-caption')),
        ready ? findsOneWidget : findsNothing,
        reason: 'the caption is what differs',
      );
      expect(_rail(tester), preview);
    });
  }
}
