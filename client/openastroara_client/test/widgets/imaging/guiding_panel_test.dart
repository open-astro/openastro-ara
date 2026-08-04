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
import 'package:openastroara/widgets/imaging/guiding_panel.dart';
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
    ProfileApi? profileApi}) async {
  final api = _FakeGuiderApi()..status = status;
  final container = ProviderContainer(overrides: [
    savedServerServiceProvider.overrideWithValue(
        _FakeSavedServerService(withServer ? const [_server] : const [])),
    guiderApiFactoryProvider.overrideWithValue((_) => api),
    // A deterministic hydrate by default — the real ProfileApi would hit the
    // test env's blocked HttpClient and leave the Apply gate in flux.
    profileApiProvider.overrideWithValue(profileApi ?? _FakeProfileApi()),
  ]);
  addTearDown(container.dispose);
  await tester.pumpWidget(UncontrolledProviderScope(
    container: container,
    child: const MaterialApp(
      home: Scaffold(body: GuidingPanel()),
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
  testWidgets('collapsed header shows the guider state and em-dash RMS '
      'when not guiding', (tester) async {
    await _pump(tester,
        status: const GuiderStatus(
          name: 'OpenAstro Guider',
          connectionState: GuiderConnectionState.connected,
          runtimeState: GuiderRuntimeState.stopped,
        ));
    expect(find.text('Guiding'), findsOneWidget);
    expect(find.text('stopped'), findsOneWidget);
    expect(find.text('RMS —'), findsOneWidget);
    // Collapsed: no controls visible.
    expect(find.text('RA aggressiveness'), findsNothing);
  });

  testWidgets('guiding: header shows live RMS; expanding shows arcsec + px '
      'cells', (tester) async {
    final container = await _pump(tester,
        status: const GuiderStatus(
          name: 'OpenAstro Guider',
          connectionState: GuiderConnectionState.connected,
          runtimeState: GuiderRuntimeState.guiding,
          rmsTotal: 0.5,
          rmsRa: 0.3,
          rmsDec: 0.4,
        ));
    expect(find.text('guiding'), findsOneWidget);
    expect(find.text('RMS 0.50″'), findsOneWidget);

    await tester.tap(find.text('Guiding'));
    await tester.pump();

    expect(find.text('0.50″'), findsOneWidget);
    expect(find.text('0.30″'), findsOneWidget);
    expect(find.text('0.40″'), findsOneWidget);
    // No guide focal length / pixel size configured → px unavailable.
    expect(find.text('— px'), findsNothing);
    expect(find.text('—'), findsNWidgets(3));

    // With the §63.5 guide train set, px derives from the image scale:
    // 206.265 * 3.75 / 200 ≈ 3.867 ″/px → 0.5″ ≈ 0.13 px.
    final phd2N = container.read(phd2SettingsProvider.notifier);
    phd2N.setGuideFocalLength(200);
    phd2N.setGuidePixelSize(3.75);
    await tester.pump();
    expect(find.text('0.13 px'), findsOneWidget);

    // The tuning controls no longer live inline — they open in the dialog.
    expect(find.text('RA aggressiveness'), findsNothing);

    await _teardownPanel(tester, container);
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
      profileApiProvider.overrideWith((ref) => ref.watch(_apiSwitchProvider)),
    ]);
    addTearDown(container.dispose);
    await tester.pumpWidget(UncontrolledProviderScope(
      container: container,
      child: const MaterialApp(home: Scaffold(body: GuidingPanel())),
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
            () async => throw StateError('daemon unreachable')));
    await tester.tap(find.byTooltip('Tune guiding…'));
    await tester.pump();
    await tester.pump();

    expect(find.textContaining('Could not load saved values'), findsOneWidget);
    expect(applyButton(tester).onPressed, isNull,
        reason: 'applying defaults would clobber the daemon-side profile');

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
