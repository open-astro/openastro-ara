import 'dart:io' show File, Platform;
import 'dart:typed_data' show ByteData;

import 'package:flutter/material.dart';
import 'package:flutter/rendering.dart' show RenderParagraph;
import 'package:flutter/services.dart' show FontLoader;
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_riverpod/misc.dart' show Override;
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/models/calibration/calibration_models.dart'
    show DarkLibraryState;
import 'package:openastroara/models/calibration_status.dart';
import 'package:openastroara/models/camera_status.dart';
import 'package:openastroara/models/guider_status.dart';
import 'package:openastroara/models/server.dart';
import 'package:openastroara/screens/simple/simple_calibration.dart'
    show
        SimpleCalibrationShutterCaption,
        SimpleCalibrationTab,
        SimpleCalibrationTabNotifier,
        simpleCalibrationTabProvider;
import 'package:openastroara/screens/simple/simple_shell.dart';
import 'package:openastroara/services/guider_api.dart' show GuiderClient;
import 'package:openastroara/services/simple_mode_prefs_service.dart';
import 'package:openastroara/state/app_shell_state.dart';
import 'package:openastroara/state/calibration/calibration_state.dart'
    show DarkLibraryStatusNotifier, darkLibraryStatusProvider;
import 'package:openastroara/state/equipment/camera_state.dart';
import 'package:openastroara/state/guider/guide_step_state.dart';
import 'package:openastroara/state/guider/guider_calibration_state.dart';
import 'package:openastroara/state/guider/guider_state.dart';
import 'package:openastroara/state/server_state.dart'
    show discoveredServersProvider;
import 'package:openastroara/state/settings/settings_nav.dart'
    show kLiveTabIndex;
import 'package:openastroara/state/simple_calibration_state.dart'
    show simpleLibraryNeedProvider;
import 'package:openastroara/state/simple_mode_state.dart';
import 'package:openastroara/state/simple_preflight_state.dart'
    show railFigureProvider, simpleShowingProvider;

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

/// A looping guider with a step on its trace, so the bottom row shows it.
class _Looping extends GuiderStatusNotifier {
  @override
  Future<void> refresh([GuiderClient? client]) async {}
  @override
  Future<GuiderStatus?> build() async => GuiderStatus.fromJson({
    'device_id': 'g',
    'name': 'PHD2',
    'state': 'connected',
    'runtime': {'state': 'looping'},
  });
}

class _OneStep extends GuideStepNotifier {
  @override
  List<GuideStep> build() => [
    GuideStep(at: DateTime.utc(2026), raPx: 0.1, decPx: -0.1),
  ];
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

class _DarksTab extends SimpleCalibrationTabNotifier {
  @override
  SimpleCalibrationTab build() => SimpleCalibrationTab.darks;
}

class _Camera extends CameraStatusNotifier {
  @override
  Future<CameraStatus?> build() async => CameraStatus.fromJson({
    'device_id': 'c',
    'name': 'ASI2600MM',
    'state': 'connected',
  });
}

/// A library with nothing on file: every exposure and the bias to build.
class _EmptyLibrary extends DarkLibraryStatusNotifier {
  @override
  Future<DarkLibraryState?> build() async => const DarkLibraryState(
    status: 'idle',
    totalCombinations: 0,
    completedCombinations: 0,
    generatedSequenceId: null,
    entries: [],
  );
}

Future<ProviderContainer> _pump(
  WidgetTester tester,
  List<Override> extra, {
  Size size = const Size(1280, 720),
  double text = 1,
  bool phone = false,
}) async {
  tester.view.physicalSize = size;
  tester.view.devicePixelRatio = 1;
  addTearDown(tester.view.reset);
  final container = ProviderContainer(
    overrides: [
      companionPlatformProvider.overrideWithValue(
        phone ? TargetPlatform.iOS : TargetPlatform.linux,
      ),
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
      child: MaterialApp(
        builder: (context, child) => MediaQuery(
          data: MediaQuery.of(context)
              .copyWith(textScaler: TextScaler.linear(text)),
          child: child!,
        ),
        home: const SimpleShell(planetarium: SizedBox(key: Key('sky'))),
      ),
    ),
  );
  await tester.pump();
  return container;
}

// The rail is the panel round the shutter: the tab list on a tablet or
// desktop, the mode button and grid on a phone held sideways.
Rect _rail(WidgetTester tester) => tester.getRect(
  find
      .ancestor(
        of: find.byKey(const Key('simple-shutter')),
        matching: find.byType(SimplePanel),
      )
      .first,
);

// How far the tab list scrolls: none while the rail holds every tab.
double _scroll(WidgetTester tester) => tester
    .state<ScrollableState>(
      find
          .descendant(
            of: find.byKey(const Key('simple-rail-tabs')),
            matching: find.byType(Scrollable),
          )
          .first,
    )
    .position
    .maxScrollExtent;

Future<void> _open(WidgetTester tester, String slug) async {
  final tab = find.byKey(Key('simple-rail-tab-$slug'));
  if (tab.evaluate().isEmpty) {
    await tester.tap(find.byKey(const Key('simple-mode')));
    await tester.pumpAndSettle();
    await tester.tap(find.byKey(Key('simple-mode-$slug')));
  } else {
    await tester.ensureVisible(tab);
    await tester.tap(tab);
  }
  await tester.pumpAndSettle();
}

final _ready = <Override>[
  guiderStatusProvider.overrideWith(_Guider.new),
  guiderCalibrationProvider.overrideWith(_GuiderCal.new),
  simpleCalibrationTabProvider.overrideWith(_GuideTab.new),
];

// The longest caption: the Library's, which names every exposure to shoot.
final _longJob = <Override>[
  cameraStatusProvider.overrideWith(_Camera.new),
  darkLibraryStatusProvider.overrideWith(_EmptyLibrary.new),
  simpleLibraryNeedProvider.overrideWithValue((
    exposures: [30, 60, 120, 300],
    gain: 100,
    offset: null,
    temperatureC: null,
  )),
  simpleCalibrationTabProvider.overrideWith(_DarksTab.new),
];

final _caption = find.byKey(const Key('simple-calibration-shutter-caption'));

void main() {
  for (final (where, size, text, roomy) in const [
    // Only 1280x900 is tall enough for the slot (kSimpleRailRoomyHeight).
    ('1280x720 at 1.0x', Size(1280, 720), 1.0, false),
    ('1280x900 at 1.0x', Size(1280, 900), 1.0, true),
    ('1024x560 at 1.0x', Size(1024, 560), 1.0, false),
    ('1280x720 at 1.5x', Size(1280, 720), 1.5, false),
  ]) {
    for (final (name, ready) in const [
      ('idle', false),
      ('with a job ready', true),
    ]) {
      testWidgets('the rail is the same size on Preview and Calibration, '
          '$name, $where (#2038)', (tester) async {
        await _pump(tester, [if (ready) ..._ready], size: size, text: text);
        await _open(tester, 'preview');
        final preview = _rail(tester);
        await _open(tester, 'calibration');
        // Whether the caption fits the slot is measured with Roboto below:
        // the test font's square glyphs fit fewer words.
        if (!roomy) {
          expect(_caption, findsNothing, reason: 'no slot without room');
        }
        expect(_rail(tester), preview);
        expect(tester.takeException(), isNull);
      });
    }
  }

  // The tabs in the rail's order.
  for (final slug in const [
    'preview',
    'focus',
    'polar',
    'guider',
    'plan',
    'calibration',
  ]) {
    testWidgets('at 1280x720 and 1.0x the tab list does not scroll with '
        '$slug open (#2038)', (tester) async {
      await _pump(tester, _ready);
      await _open(tester, slug);
      expect(_scroll(tester), 0);
      expect(tester.takeException(), isNull);
    });
  }

  // From 832 high (kSimpleRailRoomyHeight) the rail at 1.0x holds the slot
  // with its tabs unscrolled at its fullest: the bottom row showing and Plan
  // open with its figure. A safe area is not room.
  for (final (where, size, padding, roomy) in const [
    ('831 high', Size(1280, 831), FakeViewPadding.zero, false),
    ('832 high', Size(1280, 832), FakeViewPadding.zero, true),
    (
      '875 high with a 44 px safe area',
      Size(1280, 875),
      FakeViewPadding(top: 24, bottom: 20),
      false,
    ),
    (
      '876 high with a 44 px safe area',
      Size(1280, 876),
      FakeViewPadding(top: 24, bottom: 20),
      true,
    ),
  ]) {
    testWidgets('the slot is on every mode only where the rail holds it '
        'unscrolled at its fullest, $where (#2038)', (tester) async {
      tester.view.padding = padding;
      await _pump(tester, [
        ..._longJob,
        guiderStatusProvider.overrideWith(_Looping.new),
        guideStepsProvider.overrideWith(_OneStep.new),
        railFigureProvider.overrideWithValue('12/40'),
      ], size: size);
      expect(find.byKey(const Key('simple-guide-trace')), findsOneWidget);
      await _open(tester, 'preview');
      final preview = _rail(tester);
      await _open(tester, 'plan');
      expect(find.byKey(const Key('simple-rail-figure')), findsOneWidget);
      expect(_scroll(tester), 0, reason: 'Plan, its figure and the slot');
      await _open(tester, 'calibration');
      expect(
        find.byType(SimpleCalibrationShutterCaption),
        roomy ? findsOneWidget : findsNothing,
      );
      expect(
        tester
            .widget<Tooltip>(
              find.ancestor(
                of: find.byKey(const Key('simple-shutter')),
                matching: find.byType(Tooltip),
              ),
            )
            .message,
        'Build 30 s · 1 min · 2 min · 5 min + bias',
      );
      expect(_scroll(tester), 0);
      expect(_rail(tester), preview);
      expect(tester.takeException(), isNull);
    });
  }

  testWidgets('where the caption does not fit it is the shutter tooltip and '
      'label (#2038)', (tester) async {
    await _pump(tester, _ready, size: const Size(1024, 560));
    await _open(tester, 'calibration');
    final caption = tester
        .widget<Tooltip>(
          find.ancestor(
            of: find.byKey(const Key('simple-shutter')),
            matching: find.byType(Tooltip),
          ),
        )
        .message!;
    expect(caption, isNotEmpty);
    final handle = tester.ensureSemantics();
    expect(
      tester.getSemantics(find.byKey(const Key('simple-shutter'))).label,
      caption,
    );
    handle.dispose();
  });

  // The phone's deck keeps the caption under the shutter; an idle shutter has
  // none, so the deck keeps Preview's size and its top stays put.
  testWidgets('the phone deck is the same size on Preview and on an idle '
      'Calibration (#2038)', (tester) async {
    final container = await _pump(
      tester,
      const [],
      size: const Size(390, 844),
      phone: true,
    );
    final deck = find.byKey(const Key('companion-deck'));
    Future<Rect> open(SimpleModeScreen mode) async {
      container.read(simpleShowingProvider.notifier).pick(mode);
      await tester.pumpAndSettle();
      return tester.getRect(deck);
    }

    final preview = await open(SimpleModeScreen.preview);
    expect(await open(SimpleModeScreen.calibration), preview);
  });

  // The test font draws every glyph as a square; the caption's fit is measured
  // with the Roboto the Flutter SDK ships (as simple_text_scale_test does).
  final fonts =
      '${Platform.environment['FLUTTER_ROOT']}/bin/cache/artifacts/material_fonts';
  final hasFonts = File('$fonts/Roboto-Regular.ttf').existsSync();
  Future<void> roboto() =>
      (FontLoader('Roboto')..addFont(
            Future.value(
              ByteData.view(
                File('$fonts/Roboto-Regular.ttf').readAsBytesSync().buffer,
              ),
            ),
          ))
          .load();

  for (final text in const [1.0, 1.3]) {
    testWidgets(
      'the Guide caption is not shrunk in its slot and does not widen the '
      'rail at ${text}x (#2038)',
      skip: !hasFonts,
      (tester) async {
        await roboto();
        await _pump(tester, _ready, size: const Size(1280, 900), text: text);
        await _open(tester, 'preview');
        final preview = _rail(tester);
        await _open(tester, 'calibration');
        final slot = tester.getSize(
          find
              .ancestor(of: _caption, matching: find.byType(LayoutBuilder))
              .first,
        );
        expect(
          find.ancestor(of: _caption, matching: find.byType(FittedBox)),
          findsNothing,
          reason: 'drawn at full size, never scaled down',
        );
        expect(tester.getSize(_caption).width, slot.width);
        expect(
          tester
              .renderObject<RenderParagraph>(_caption)
              .getMaxIntrinsicHeight(slot.width),
          lessThanOrEqualTo(slot.height),
          reason: 'within the three lines of the slot, so never cut',
        );
        // Above 1.1x the rail leaves more room than its buttons' width.
        expect(_rail(tester), preview, reason: 'as wide as the rail buttons');
      },
    );

    testWidgets(
      'the Library caption that does not fit three lines at ${text}x is the '
      'tooltip, not a shrunk caption (#2038)',
      skip: !hasFonts,
      (tester) async {
        await roboto();
        await _pump(tester, _longJob, size: const Size(1280, 900), text: text);
        await _open(tester, 'preview');
        final preview = _rail(tester);
        await _open(tester, 'calibration');
        expect(find.byType(SimpleCalibrationShutterCaption), findsOneWidget);
        expect(_caption, findsNothing);
        expect(
          tester
              .widget<Tooltip>(
                find.ancestor(
                  of: find.byKey(const Key('simple-shutter')),
                  matching: find.byType(Tooltip),
                ),
              )
              .message,
          'Build 30 s · 1 min · 2 min · 5 min + bias',
        );
        expect(_rail(tester), preview);
        expect(tester.takeException(), isNull);
      },
    );
  }
}
