import 'dart:async';

import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/models/guider_status.dart';
import 'package:openastroara/models/server.dart';
import 'package:openastroara/services/guider_api.dart';
import 'package:openastroara/services/profile_api.dart';
import 'package:openastroara/services/saved_server_service.dart';
import 'package:openastroara/state/guider/guider_state.dart';
import 'package:openastroara/state/profile_management_state.dart';
import 'package:openastroara/state/settings/phd2_settings_state.dart';
import 'package:openastroara/state/saved_server_state.dart';
import 'package:openastroara/models/ws_event.dart';
import 'package:openastroara/state/guider/guide_graph_settings.dart';
import 'package:openastroara/state/guider/guide_step_state.dart';
import 'package:openastroara/state/ws/ws_providers.dart';
import 'package:openastroara/widgets/imaging/guiding_strip.dart';
import 'package:openastroara/widgets/imaging/guiding_tune_dialog.dart';

class _FakeSavedServerService implements SavedServerService {
  _FakeSavedServerService(List<AraServer> stored)
      : _stored = List.of(stored); // growable — add() switches the active server
  final List<AraServer> _stored;
  @override
  Future<List<AraServer>> loadAll() async => List.unmodifiable(_stored);
  @override
  Future<void> saveAll(List<AraServer> servers) async {}
  @override
  Future<void> add(AraServer server) async {
    // Mirror the real service's move-to-end so add() actually switches the
    // active server (the server-switch memo test depends on it).
    _stored
      ..removeWhere((s) => s == server)
      ..add(server);
  }
}

class _FakeGuiderApi implements GuiderClient {
  GuiderStatus? status;
  @override
  Future<GuiderStatus?> getStatus() async => status;
  @override
  void close() {}
  @override
  Future<void> connect(
      {String host = kDefaultGuiderHost, int port = kDefaultGuiderPort}) async {}
  @override
  Future<void> disconnect() async {}
}

const _server = AraServer(hostname: 'h', port: 5555);

/// Swappable profile-API source for the late-appearing-API test.
class _ApiSwitchNotifier extends Notifier<ProfileApi?> {
  @override
  ProfileApi? build() => null;
  void set(ProfileApi? value) => state = value;
}

final _apiSwitchProvider =
    NotifierProvider<_ApiSwitchNotifier, ProfileApi?>(_ApiSwitchNotifier.new);

/// Pure [ProfileApi] fake — the hydrate/apply round-trip without Dio. The
/// default loader resolves immediately with the client defaults.
class _FakeProfileApi extends ProfileApi {
  _FakeProfileApi([this._load]) : super(_server);
  final Future<Phd2Settings> Function()? _load;
  @override
  Future<Phd2Settings> getPhd2Settings() =>
      _load != null ? _load() : Future.value(const Phd2Settings());
  @override
  Future<Phd2Settings> putPhd2Settings(Phd2Settings value) async => value;
}

Future<ProviderContainer> _pump(WidgetTester tester,
    {GuiderStatus? status,
    bool withServer = true,
    ProfileApi? profileApi,
    Stream<WsEvent>? ws}) async {
  final api = _FakeGuiderApi()..status = status;
  final container = ProviderContainer(overrides: [
    savedServerServiceProvider.overrideWithValue(
        _FakeSavedServerService(withServer ? const [_server] : const [])),
    guiderApiFactoryProvider.overrideWithValue((_) => api),
    // The strip's guide-step buffer listens to the WS stream; a real stream
    // would try to dial the fake server.
    wsEventsProvider.overrideWith((ref) => ws ?? const Stream<WsEvent>.empty()),
    // A deterministic hydrate by default — the real ProfileApi would hit the
    // test env's blocked HttpClient and leave the Apply gate in flux.
    profileApiProvider.overrideWithValue(profileApi ?? _FakeProfileApi()),
  ]);
  addTearDown(container.dispose);
  // Steps and markers are stamped on arrival. On Windows (~15 ms clock) three
  // steps and a dither can share one DateTime.now(), which puts the steps
  // inside the dither's settle window and out of the RMS. One shared clock
  // that moves on every stamp keeps the arrival order the test wrote.
  var clock = DateTime.utc(2026, 10, 4, 21);
  DateTime tick() => clock = clock.add(const Duration(milliseconds: 100));
  container.read(guideStepsProvider.notifier).now = tick;
  container.read(guideMarkersProvider.notifier).now = tick;
  await tester.pumpWidget(UncontrolledProviderScope(
    container: container,
    child: const MaterialApp(
      home: Scaffold(body: GuidingStrip()),
    ),
  ));
  // Let saved servers load + the initial status read land.
  await tester.pump();
  await tester.pump();
  return container;
}

/// Tears down the panel AND its container so the autoDispose live-RMS poller
/// (a periodic timer) is cancelled before the binding's pending-timer check —
/// riverpod's deferred autoDispose doesn't run early enough under testWidgets.
Future<void> _teardownPanel(WidgetTester tester, ProviderContainer c) async {
  await tester.pumpWidget(const SizedBox());
  c.dispose();
  await tester.pump();
}

void main() {
  testWidgets('open by default: header shows the guider state and em-dash '
      'RMS when not guiding; the graph says so; the header tap collapses it',
      (tester) async {
    final container = await _pump(tester,
        status: const GuiderStatus(
          name: 'OpenAstro Guider',
          connectionState: GuiderConnectionState.connected,
          runtimeState: GuiderRuntimeState.stopped,
        ));
    expect(find.text('Guiding'), findsOneWidget);
    expect(find.text('stopped'), findsOneWidget);
    expect(find.text('RMS —'), findsOneWidget);
    expect(find.text('Not guiding'), findsOneWidget);
    // Telemetry only — no tuning controls inline.
    expect(find.text('RA aggressiveness'), findsNothing);

    await tester.tap(find.text('Guiding'));
    await tester.pump();
    expect(find.text('Not guiding'), findsNothing);
    expect(container.read(guidingStripExpandedProvider), isFalse);
    // Re-open so the next test's default is untouched (root provider, but a
    // fresh container per test — this is belt and braces).
    await tester.tap(find.text('Guiding'));
    await tester.pump();
    await _teardownPanel(tester, container);
  });

  testWidgets('a disconnected guider reads as such in header and graph',
      (tester) async {
    final container = await _pump(tester, status: null);
    expect(find.text('disconnected'), findsOneWidget);
    expect(find.text('Guider not connected'), findsOneWidget);
    await _teardownPanel(tester, container);
  });

  WsEvent step(int i, {double? raArcsec, double? decArcsec, double? scale}) =>
      WsEvent(
        type: 'guider.step',
        ts: DateTime.utc(2026, 10, 4, 21, 0, i),
        seq: i + 1,
        payload: <String, dynamic>{
          'frame': i,
          'ra_raw_px': 0.2 * i,
          'dec_raw_px': -0.1 * i,
          'ra_arcsec': ?raArcsec,
          'dec_arcsec': ?decArcsec,
          'ra_duration_ms': 50,
          'dec_duration_ms': -20,
          'pixel_scale_arcsec': ?scale,
        },
      );

  testWidgets('pixel-only steps plot and read in px until a scale is known, '
      'then in arcsec from the guide train', (tester) async {
    final ws = StreamController<WsEvent>.broadcast();
    addTearDown(ws.close);
    final container = await _pump(tester,
        status: const GuiderStatus(
          name: 'OpenAstro Guider',
          connectionState: GuiderConnectionState.connected,
          runtimeState: GuiderRuntimeState.guiding,
        ),
        ws: ws.stream);
    expect(find.text('guiding'), findsOneWidget);
    expect(find.text('Waiting for guide frames…'), findsOneWidget);
    expect(find.text('RMS —'), findsOneWidget);

    // ra px: 0, 0.2, 0.4; dec px: 0, -0.1, -0.2 → RMS tot = sqrt(mean(ra²+dec²))
    // = sqrt((0 + 0.05 + 0.20) / 3) = 0.2887 px.
    for (var i = 0; i < 3; i++) {
      ws.add(step(i));
    }
    await tester.pump();
    await tester.pump();
    expect(find.text('RMS 0.29 px'), findsOneWidget);
    expect(find.text('0.29 px'), findsOneWidget, reason: 'RMS Tot row');
    expect(find.text('0.40 px'), findsOneWidget, reason: 'Peak RA row');
    expect(find.text('no scale'), findsOneWidget);
    expect(find.text('3 of 3 frames · px'), findsOneWidget);

    // With the §63.5 guide train set the pixels convert:
    // 206.265 * 3.75 / 200 ≈ 3.867 ″/px → 0.2887 px ≈ 1.12″.
    final phd2N = container.read(phd2SettingsProvider.notifier);
    phd2N.setGuideFocalLength(200);
    phd2N.setGuidePixelSize(3.75);
    await tester.pump();
    expect(find.text('RMS 1.12″'), findsOneWidget);
    expect(find.text('3.87″/px'), findsOneWidget);
    expect(find.text('3 of 3 frames · arc-sec'), findsOneWidget);

    // PHD2's units toggle forces px again.
    container.read(guideGraphSettingsProvider.notifier).setUnit(GuideGraphUnit.px);
    await tester.pump();
    expect(find.text('RMS 0.29 px'), findsOneWidget);

    // The tuning controls no longer live inline — they open in the dialog.
    expect(find.text('RA aggressiveness'), findsNothing);

    await _teardownPanel(tester, container);
  });

  testWidgets('guider.step events with the guider\'s own scale fill the graph '
      'in arcsec; markers draw; Clear empties both', (tester) async {
    final ws = StreamController<WsEvent>.broadcast();
    addTearDown(ws.close);
    final container = await _pump(tester,
        status: const GuiderStatus(
          name: 'OpenAstro Guider',
          connectionState: GuiderConnectionState.connected,
          runtimeState: GuiderRuntimeState.guiding,
        ),
        ws: ws.stream);
    for (var i = 0; i < 3; i++) {
      ws.add(step(i, raArcsec: 0.4 * i, decArcsec: -0.2 * i, scale: 2.0));
    }
    ws.add(WsEvent(
        type: 'guider.event',
        ts: DateTime.utc(2026, 10, 4, 21, 0, 5),
        seq: 9,
        payload: const <String, dynamic>{'kind': 'dithered', 'dx_px': 2, 'dy_px': -1}));
    await tester.pump();
    await tester.pump();
    expect(container.read(guideStepsProvider).length, 3);
    expect(container.read(guideMarkersProvider).single.kind, GuideMarkerKind.dithered);
    expect(find.text('Waiting for guide frames…'), findsNothing);
    final paint = tester.widget<CustomPaint>(find.byWidgetPredicate(
        (w) => w is CustomPaint && w.painter is GuideGraphPainter));
    expect(paint.painter, isA<GuideGraphPainter>());
    // ra″: 0, 0.4, 0.8; dec″: 0, −0.2, −0.4 → tot = sqrt((0 + 0.2 + 0.8)/3) = 0.577″.
    expect(find.text('RMS 0.58″'), findsOneWidget);
    expect(find.text('2.00″/px'), findsOneWidget);
    expect(find.text('3 of 3 frames · arc-sec'), findsOneWidget);

    // The control row scrolls sideways at the test's 800 px width.
    await tester.ensureVisible(find.text('Clear'));
    await tester.tap(find.text('Clear'));
    await tester.pump();
    expect(container.read(guideStepsProvider), isEmpty);
    expect(container.read(guideMarkersProvider), isEmpty);
    expect(find.text('Waiting for guide frames…'), findsOneWidget);
    await _teardownPanel(tester, container);
  });

  testWidgets('the collapsed header keeps settle frames out of its RMS, as '
      'the open graph does', (tester) async {
    final ws = StreamController<WsEvent>.broadcast();
    addTearDown(ws.close);
    final container = await _pump(tester,
        status: const GuiderStatus(
          name: 'OpenAstro Guider',
          connectionState: GuiderConnectionState.connected,
          runtimeState: GuiderRuntimeState.guiding,
        ),
        ws: ws.stream);
    for (var i = 0; i < 3; i++) {
      ws.add(step(i, raArcsec: 0.4 * i, decArcsec: -0.2 * i, scale: 2.0));
    }
    // A dither at 21:00:02.5 with no settle_done yet: frames 3 and 4 are the
    // dither's own excursion, drawn but kept out of the stats.
    ws.add(WsEvent(
        type: 'guider.event',
        ts: DateTime.utc(2026, 10, 4, 21, 0, 2, 500),
        seq: 9,
        payload: const <String, dynamic>{'kind': 'dithered', 'dx_px': 2, 'dy_px': -1}));
    for (var i = 3; i < 5; i++) {
      ws.add(step(i, raArcsec: 5.0, decArcsec: -4.0, scale: 2.0));
    }
    await tester.pump();
    await tester.pump();
    expect(find.text('RMS 0.58″'), findsOneWidget);

    container.read(guidingStripExpandedProvider.notifier).toggle();
    await tester.pump();
    expect(container.read(guidingStripExpandedProvider), isFalse);
    expect(find.text('RMS 0.58″'), findsOneWidget,
        reason: 'collapsing must not fold the dither into the one-line status');
    await _teardownPanel(tester, container);
  });

  test('GuideGraphModel: unit choice, visible window and auto y-range follow '
      'PHD2', () {
    final t = DateTime.utc(2026);
    GuideGraphModel model(List<GuideStep> steps,
            {GuideGraphSettings settings = const GuideGraphSettings(),
            double? fallbackScale}) =>
        GuideGraphModel(
            steps: steps, markers: const [], settings: settings, fallbackScale: fallbackScale);

    final arcsec = model([
      GuideStep(at: t, raPx: 0.5, decPx: 0.1, raArcsec: 1.3, decArcsec: 0.2, pixelScaleArcsec: 2.6),
      GuideStep(at: t, raPx: 0.1, decPx: -0.9, raArcsec: 0.2, decArcsec: -1.8, pixelScaleArcsec: 2.6),
    ]);
    expect(arcsec.inArcsec, isTrue);
    expect(arcsec.yHalfRange, 2.0);
    expect(arcsec.stats.peakDec, 1.8);

    final px = model([GuideStep(at: t, raPx: 0.3, decPx: -0.2)]);
    expect(px.inArcsec, isFalse);
    expect(px.yHalfRange, 0.5);

    // A client-side scale converts pixel-only steps to arcsec.
    final converted = model([GuideStep(at: t, raPx: 1.0, decPx: 0.0)], fallbackScale: 3.0);
    expect(converted.inArcsec, isTrue);
    expect(converted.yHalfRange, 4.0);

    // Forced px ignores the scale; forced arcsec with no scale plots nothing.
    expect(model([GuideStep(at: t, raPx: 1.0, decPx: 0.0)],
            fallbackScale: 3.0, settings: const GuideGraphSettings(unit: GuideGraphUnit.px))
        .inArcsec, isFalse);
    final blind = model([GuideStep(at: t, raPx: 1.0, decPx: 0.0)],
        settings: const GuideGraphSettings(unit: GuideGraphUnit.arcsec));
    expect(blind.inArcsec, isTrue);
    expect(blind.stats.samples, 0);

    // Fixed y wins over auto; the window is the newest xRange frames.
    final many = model(
        [for (var i = 0; i < 120; i++) GuideStep(at: t, raPx: i.toDouble(), decPx: 0)],
        settings: const GuideGraphSettings(xRange: 50, yHalfRange: 1));
    expect(many.visible.length, 50);
    expect(many.visible.first.raPx, 70);
    expect(many.yHalfRange, 1);

    // Beyond the ladder the top rung holds (clipped, not unbounded).
    expect(model([GuideStep(at: t, raPx: 0, decPx: 0, raArcsec: 40, decArcsec: 0, pixelScaleArcsec: 2)]).yHalfRange, 16.0);

    // Frames inside a dither's settle window are drawn but, as in PHD2,
    // kept out of the stats and the auto y range.
    final dithered = GuideGraphModel(
      steps: [
        for (var i = 0; i < 10; i++)
          GuideStep(at: t.add(Duration(seconds: i)), raPx: 0.1, decPx: 0.1, raArcsec: 0.2, decArcsec: 0.2, pixelScaleArcsec: 2),
        for (var i = 10; i < 14; i++)
          GuideStep(at: t.add(Duration(seconds: i)), raPx: 5, decPx: 5, raArcsec: 10, decArcsec: 10, pixelScaleArcsec: 2),
        for (var i = 14; i < 20; i++)
          GuideStep(at: t.add(Duration(seconds: i)), raPx: 0.1, decPx: 0.1, raArcsec: 0.2, decArcsec: 0.2, pixelScaleArcsec: 2),
      ],
      markers: [
        GuideMarker(at: t.add(const Duration(seconds: 10, milliseconds: -500)), kind: GuideMarkerKind.dithered),
        GuideMarker(at: t.add(const Duration(seconds: 14, milliseconds: -500)), kind: GuideMarkerKind.settleDone, status: 0),
      ],
      settings: const GuideGraphSettings(),
      fallbackScale: null,
    );
    expect(dithered.isSettling(t.add(const Duration(seconds: 11))), isTrue);
    expect(dithered.isSettling(t.add(const Duration(seconds: 15))), isFalse);
    expect(dithered.stats.samples, 16);
    expect(dithered.stats.peakRa, 0.2);
    expect(dithered.yHalfRange, 0.5);
  });

  testWidgets('the Tune dialog shows the runtime-safe controls only',
      (tester) async {
    final container = await _pump(tester,
        status: const GuiderStatus(
          name: 'OpenAstro Guider',
          connectionState: GuiderConnectionState.connected,
          runtimeState: GuiderRuntimeState.guiding,
          rmsTotal: 0.5,
        ));
    await tester.tap(find.byTooltip('Tune guiding…'));
    await tester.pumpAndSettle();

    expect(find.byType(GuidingTuneDialog), findsOneWidget);
    expect(find.text('RA aggressiveness'), findsOneWidget);
    expect(find.text('Dec aggressiveness'), findsOneWidget);
    expect(find.text('Minimum move (px)'), findsOneWidget);
    expect(find.text('Dec guide mode'), findsOneWidget);
    expect(find.text('Dither pixels'), findsOneWidget);
    expect(find.text('Guide camera'), findsNothing);
    expect(find.text('Applies live — guiding is not interrupted.'),
        findsOneWidget);
    // Default aggressiveness 0.7 renders as a percent.
    expect(find.text('70%'), findsNWidgets(2));

    await _teardownPanel(tester, container);
  });

  FilledButton applyButton(WidgetTester tester) =>
      tester.widget<FilledButton>(find
          .ancestor(
              of: find.text('Apply'),
              matching: find.bySubtype<FilledButton>())
          .first);

  testWidgets('Apply is disabled until the initial hydrate succeeds — a '
      'full-object PUT must never run from client defaults', (tester) async {
    final gate = Completer<Phd2Settings>();
    final container = await _pump(tester,
        status: const GuiderStatus(
          name: 'OpenAstro Guider',
          connectionState: GuiderConnectionState.connected,
          runtimeState: GuiderRuntimeState.guiding,
          rmsTotal: 0.5,
        ),
        profileApi: _FakeProfileApi(() => gate.future));
    await tester.tap(find.byTooltip('Tune guiding…'));
    await tester.pump();
    await tester.pump();
    expect(applyButton(tester).onPressed, isNull,
        reason: 'hydrate has not resolved yet');

    gate.complete(const Phd2Settings(host: 'daemon.local'));
    await tester.pump();
    expect(applyButton(tester).onPressed, isNotNull);
    expect(container.read(phd2SettingsProvider).host, 'localhost',
        reason: 'the dialog seeds its own draft — the shared provider is untouched');

    await _teardownPanel(tester, container);
  });

  testWidgets('a late-appearing profile API still hydrates an open dialog',
      (tester) async {
    // The dialog can open before saved servers resolve (profile API null).
    // The listenManual retry must hydrate once the API appears — otherwise
    // Apply stays silently disabled for the whole dialog session.
    final api = _FakeGuiderApi()
      ..status = const GuiderStatus(
        name: 'OpenAstro Guider',
        connectionState: GuiderConnectionState.connected,
        runtimeState: GuiderRuntimeState.guiding,
        rmsTotal: 0.5,
      );
    final container = ProviderContainer(overrides: [
      savedServerServiceProvider
          .overrideWithValue(_FakeSavedServerService(const [_server])),
      guiderApiFactoryProvider.overrideWithValue((_) => api),
      wsEventsProvider.overrideWith((ref) => const Stream<WsEvent>.empty()),
      profileApiProvider.overrideWith((ref) => ref.watch(_apiSwitchProvider)),
    ]);
    addTearDown(container.dispose);
    await tester.pumpWidget(UncontrolledProviderScope(
      container: container,
      child: const MaterialApp(home: Scaffold(body: GuidingStrip())),
    ));
    await tester.pump();
    await tester.pump();

    await tester.tap(find.byTooltip('Tune guiding…'));
    await tester.pump();
    await tester.pump();
    expect(applyButton(tester).onPressed, isNull,
        reason: 'no profile API yet — hydrate could not run');

    container.read(_apiSwitchProvider.notifier).set(_FakeProfileApi());
    await tester.pump();
    await tester.pump();
    expect(applyButton(tester).onPressed, isNotNull,
        reason: 'the late-appearing API must trigger the hydrate retry');

    await _teardownPanel(tester, container);
  });

  testWidgets('edits are a local draft: Done discards, provider untouched '
      'until Apply', (tester) async {
    final container = await _pump(tester,
        status: const GuiderStatus(
          name: 'OpenAstro Guider',
          connectionState: GuiderConnectionState.connected,
          runtimeState: GuiderRuntimeState.guiding,
          rmsTotal: 0.5,
        ));
    await tester.tap(find.byTooltip('Tune guiding…'));
    await tester.pumpAndSettle();

    // Drag the RA slider: the DRAFT changes (dialog shows 100%), the shared
    // provider does not — unapplied edits never sit in shared state, so no
    // other surface's hydrate/save can leak or clobber them.
    await tester.drag(find.byType(Slider).first, const Offset(400, 0));
    await tester.pump();
    expect(find.text('100%'), findsOneWidget);
    expect(container.read(phd2SettingsProvider).raAggressiveness, 0.7,
        reason: 'provider unchanged until Apply');

    // Done discards the draft; reopening shows the provider values again.
    await tester.tap(find.text('Done'));
    await tester.pumpAndSettle();
    await tester.tap(find.byTooltip('Tune guiding…'));
    await tester.pumpAndSettle();
    expect(find.text('70%'), findsNWidgets(2),
        reason: 'the discarded draft must not survive a reopen');

    await _teardownPanel(tester, container);
  });

  testWidgets('opening the dialog never clobbers staged Settings edits '
      'in the shared provider', (tester) async {
    final container = await _pump(tester,
        status: const GuiderStatus(
          name: 'OpenAstro Guider',
          connectionState: GuiderConnectionState.connected,
          runtimeState: GuiderRuntimeState.guiding,
          rmsTotal: 0.5,
        ));
    // Simulate an unsaved Settings → Guider edit staged in the shared provider.
    container.read(phd2SettingsProvider.notifier).setHost('edited.local');

    await tester.tap(find.byTooltip('Tune guiding…'));
    await tester.pumpAndSettle();
    expect(container.read(phd2SettingsProvider).host, 'edited.local',
        reason: 'the dialog hydrates its own draft, never the shared provider');

    // Apply persists daemon-copy + tuning fields and touches only the five
    // tuning fields in the provider — the staged host edit survives.
    await tester.drag(find.byType(Slider).first, const Offset(400, 0));
    await tester.pump();
    await tester.tap(find.text('Apply'));
    await tester.pumpAndSettle();
    expect(container.read(phd2SettingsProvider).host, 'edited.local');
    expect(container.read(phd2SettingsProvider).raAggressiveness, 1.0);

    await _teardownPanel(tester, container);
  });

  testWidgets('invalid numeric input never enters the draft', (tester) async {
    final container = await _pump(tester,
        status: const GuiderStatus(
          name: 'OpenAstro Guider',
          connectionState: GuiderConnectionState.connected,
          runtimeState: GuiderRuntimeState.guiding,
          rmsTotal: 0.5,
        ));
    await tester.tap(find.byTooltip('Tune guiding…'));
    await tester.pumpAndSettle();

    // A negative minimum move is rejected at parse time (the notifier bound,
    // mirrored) — the field snaps back to the canonical draft value instead
    // of sitting invalid until an Apply silently drops it.
    final field = find.widgetWithText(TextField, '0.15');
    await tester.enterText(field, '-3');
    await tester.testTextInput.receiveAction(TextInputAction.done);
    await tester.pumpAndSettle();
    expect(find.widgetWithText(TextField, '0.15'), findsOneWidget,
        reason: 'invalid input snaps back to the last good value');
    expect(container.read(phd2SettingsProvider).minimumMove, 0.15);

    await _teardownPanel(tester, container);
  });

  testWidgets('Apply commits the draft to the provider', (tester) async {
    final container = await _pump(tester,
        status: const GuiderStatus(
          name: 'OpenAstro Guider',
          connectionState: GuiderConnectionState.connected,
          runtimeState: GuiderRuntimeState.guiding,
          rmsTotal: 0.5,
        ));
    await tester.tap(find.byTooltip('Tune guiding…'));
    await tester.pumpAndSettle();
    await tester.drag(find.byType(Slider).first, const Offset(400, 0));
    await tester.pump();
    await tester.tap(find.text('Apply'));
    await tester.pumpAndSettle();
    expect(container.read(phd2SettingsProvider).raAggressiveness, 1.0,
        reason: 'Apply commits the draft, then persists');

    await _teardownPanel(tester, container);
  });

  testWidgets('hydrate failure: error surfaced and Apply stays disabled',
      (tester) async {
    final container = await _pump(tester,
        status: const GuiderStatus(
          name: 'OpenAstro Guider',
          connectionState: GuiderConnectionState.connected,
          runtimeState: GuiderRuntimeState.guiding,
          rmsTotal: 0.5,
        ),
        profileApi: _FakeProfileApi(
            () async => throw StateError("Your rig didn't answer.")));
    await tester.tap(find.byTooltip('Tune guiding…'));
    await tester.pump();
    await tester.pump();

    // A StateError already carries copy written for people — friendlyError
    // passes it through rather than wrapping it in a second sentence.
    expect(find.textContaining("Your rig didn't answer."), findsOneWidget);
    expect(applyButton(tester).onPressed, isNull,
        reason: 'applying defaults would clobber the saved profile');

    await _teardownPanel(tester, container);
  });

  testWidgets('disconnected: hint shown and the controls are inert',
      (tester) async {
    final container = await _pump(tester,
        status: const GuiderStatus(
          name: 'OpenAstro Guider',
          connectionState: GuiderConnectionState.disconnected,
          runtimeState: GuiderRuntimeState.stopped,
        ));
    expect(find.text('disconnected'), findsOneWidget);

    await tester.tap(find.byTooltip('Tune guiding…'));
    await tester.pumpAndSettle();

    expect(
        find.textContaining('Guider disconnected — connect the guider'),
        findsOneWidget);
    // Controls render (saved values stay visible) but are inert, and Apply
    // is hard-disabled while disconnected.
    final ignore = tester.widget<IgnorePointer>(find
        .ancestor(
            of: find.text('RA aggressiveness'),
            matching: find.byType(IgnorePointer))
        .first);
    expect(ignore.ignoring, isTrue);
    expect(applyButton(tester).onPressed, isNull);

    await _teardownPanel(tester, container);
  });
}
