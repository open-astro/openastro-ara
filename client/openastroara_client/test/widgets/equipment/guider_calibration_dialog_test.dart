import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/models/calibration_status.dart';
import 'package:openastroara/models/server.dart';
import 'package:openastroara/services/guider_calibration_api.dart';
import 'package:openastroara/services/saved_server_service.dart';
import 'package:openastroara/state/guider/guider_calibration_state.dart';
import 'package:openastroara/state/saved_server_state.dart';
import 'package:openastroara/widgets/equipment/guider_calibration_dialog.dart';

class _FakeSavedServerService implements SavedServerService {
  @override
  Future<List<AraServer>> loadAll() async => const [AraServer(hostname: 'h', port: 5555)];
  @override
  Future<void> saveAll(List<AraServer> servers) async {}
  @override
  Future<void> add(AraServer server) async {}
}

class _FakeCalibrationClient implements GuiderCalibrationClient {
  _FakeCalibrationClient(this.response);
  CalibrationStatusResponse response;
  int darkBuilds = 0;
  int defectBuilds = 0;
  bool? darkEnabled;
  bool? defectEnabled;

  @override
  Future<CalibrationStatusResponse> getStatus() async => response;
  @override
  Future<void> buildDarkLibrary({
    int frameCount = 5,
    int? minExposureMs,
    int? maxExposureMs,
    bool clearExisting = false,
    String? notes,
    bool loadAfter = true,
  }) async =>
      darkBuilds++;
  @override
  Future<void> buildDefectMap({
    int exposureMs = 3000,
    int frameCount = 10,
    String? notes,
    bool loadAfter = true,
  }) async =>
      defectBuilds++;
  @override
  Future<void> setDarkLibraryEnabled(bool enabled) async => darkEnabled = enabled;
  @override
  Future<void> setDefectMapEnabled(bool enabled) async => defectEnabled = enabled;
  @override
  void close() {}
}

CalibrationStatusResponse _connected({
  bool darkExists = false,
  bool darkLoaded = false,
  bool autoLoadDarks = false,
  bool defectExists = false,
  bool autoLoadDefectMap = false,
}) =>
    CalibrationStatusResponse(
      connected: true,
      status: CalibrationStatus(
        profileId: 1,
        darkLibraryExists: darkExists,
        darkLibraryLoaded: darkLoaded,
        autoLoadDarks: autoLoadDarks,
        defectMapExists: defectExists,
        autoLoadDefectMap: autoLoadDefectMap,
        darkCountLoaded: darkLoaded ? 20 : null,
        darkMinExposureSecondsLoaded: darkLoaded ? 1.0 : null,
        darkMaxExposureSecondsLoaded: darkLoaded ? 4.0 : null,
      ),
    );

Widget _host(GuiderCalibrationClient fake) => ProviderScope(
      overrides: [
        savedServerServiceProvider.overrideWithValue(_FakeSavedServerService()),
        guiderCalibrationApiFactoryProvider.overrideWithValue((_) => fake),
      ],
      child: MaterialApp(
        home: Scaffold(
          body: Builder(
            builder: (context) => ElevatedButton(
              onPressed: () => showGuiderCalibrationDialog(context),
              child: const Text('open'),
            ),
          ),
        ),
      ),
    );

Future<void> _open(WidgetTester tester) async {
  await tester.tap(find.text('open'));
  await tester.pumpAndSettle();
}

void main() {
  testWidgets('shows dark-library status + loaded detail when connected', (tester) async {
    await tester.pumpWidget(_host(_FakeCalibrationClient(_connected(darkExists: true, darkLoaded: true))));
    await _open(tester);
    expect(find.text('Guider calibration'), findsOneWidget);
    expect(find.text('Dark library'), findsOneWidget);
    expect(find.text('Loaded'), findsWidgets);
    expect(find.textContaining('20 darks'), findsOneWidget);
  });

  testWidgets('not connected → manage-calibration prompt', (tester) async {
    await tester.pumpWidget(_host(_FakeCalibrationClient(const CalibrationStatusResponse(connected: false))));
    await _open(tester);
    expect(find.text('Connect the guider to manage calibration.'), findsOneWidget);
  });

  testWidgets('tapping Build dispatches a dark-library build', (tester) async {
    final fake = _FakeCalibrationClient(_connected());
    await tester.pumpWidget(_host(fake));
    await _open(tester);
    // Dark library not built → the button reads "Build".
    await tester.tap(find.widgetWithText(TextButton, 'Build').first);
    await tester.pumpAndSettle();
    expect(fake.darkBuilds, 1);
  });

  testWidgets('the enable switch reflects auto-load, not transient loaded state', (tester) async {
    // Loaded in memory but auto-load disabled — the switch must read OFF.
    final fake = _FakeCalibrationClient(_connected(darkExists: true, darkLoaded: true));
    await tester.pumpWidget(_host(fake));
    await _open(tester);
    final sw = tester.widget<Switch>(find.byType(Switch).first);
    expect(sw.value, isFalse, reason: 'bound to autoLoadDarks (false), not darkLibraryLoaded (true)');
  });

  testWidgets('toggling the dark-library switch calls setDarkLibraryEnabled', (tester) async {
    final fake = _FakeCalibrationClient(_connected(darkExists: true, darkLoaded: true, autoLoadDarks: true));
    await tester.pumpWidget(_host(fake));
    await _open(tester);
    await tester.tap(find.byType(Switch).first);
    await tester.pumpAndSettle();
    expect(fake.darkEnabled, isFalse, reason: 'auto-load was on → toggled off');
  });

  testWidgets('toggling the defect-map switch calls setDefectMapEnabled', (tester) async {
    final fake = _FakeCalibrationClient(_connected(defectExists: true, autoLoadDefectMap: false));
    await tester.pumpWidget(_host(fake));
    await _open(tester);
    // Defect map is the second artifact / second Switch.
    await tester.tap(find.byType(Switch).at(1));
    await tester.pumpAndSettle();
    expect(fake.defectEnabled, isTrue, reason: 'auto-load was off → toggled on');
  });
}
