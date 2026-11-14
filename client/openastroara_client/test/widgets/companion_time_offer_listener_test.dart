import 'dart:async';

import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/l10n/app_localizations.dart';
import 'package:openastroara/services/time_sync_api.dart';
import 'package:openastroara/services/ws_event_stream.dart';
import 'package:openastroara/state/settings/site_settings_state.dart';
import 'package:openastroara/state/simple_messages_state.dart';
import 'package:openastroara/state/simple_mode_state.dart';
import 'package:openastroara/state/time_sync_state.dart';
import 'package:openastroara/state/ws/ws_providers.dart';
import 'package:openastroara/widgets/companion_time_offer_listener.dart';

class _Rig implements TimeSyncClient {
  _Rig({required this.synced});
  final bool synced;
  final pushed = <DateTime>[];

  @override
  Future<TimeSyncState> getState() async => TimeSyncState(
    synced: synced,
    // Greenwich: the position the rig "holds", what the site fill reads back.
    location: const TimeSyncLocation(lat: 51.477833, lng: -0.0015, alt: 42),
  );

  @override
  Future<void> pushClientTime(DateTime utcNow) async => pushed.add(utcNow);

  @override
  dynamic noSuchMethod(Invocation i) => super.noSuchMethod(i);
}

void main() {
  const question =
      "The rig has no clock or site yet. Send this phone's location and time?";

  Future<(ProviderContainer, StreamController<WsConnectionState>, _Rig)> pump(
    WidgetTester tester, {
    bool companion = true,
    bool synced = false,
    Locale? locale,
  }) async {
    final link = StreamController<WsConnectionState>();
    addTearDown(link.close);
    final rig = _Rig(synced: synced);
    final container = ProviderContainer(
      overrides: [
        companionModeProvider.overrideWithValue(companion),
        simpleModeActiveProvider.overrideWithValue(true),
        wsConnectionStateProvider.overrideWith((ref) => link.stream),
        timeSyncApiProvider.overrideWithValue(rig),
      ],
    );
    addTearDown(container.dispose);
    await tester.pumpWidget(
      UncontrolledProviderScope(
        container: container,
        child: MaterialApp(
          locale: locale,
          localizationsDelegates: AppLocalizations.localizationsDelegates,
          supportedLocales: AppLocalizations.supportedLocales,
          home: const Scaffold(
            body: CompanionTimeOfferListener(child: SizedBox.shrink()),
          ),
        ),
      ),
    );
    return (container, link, rig);
  }

  Future<void> connect(
    WidgetTester tester,
    StreamController<WsConnectionState> link,
  ) async {
    link.add(WsConnectionState.connected);
    await tester.pump();
    await tester.pump();
  }

  List<String> titles(ProviderContainer c) => [
    for (final m in c.read(simpleMessagesProvider)) m.title,
  ];

  testWidgets('offers the phone location and time with no recent sync', (
    tester,
  ) async {
    final (c, link, _) = await pump(tester);
    await connect(tester, link);
    final pills = c.read(simpleMessagesProvider);
    expect(pills.single.kind, SimpleMessageKind.question);
    expect(pills.single.title, question);
    expect(pills.single.actions.map((a) => a.label), ['Not now', 'Send']);
  });

  testWidgets('says nothing off a phone', (tester) async {
    final (c, link, _) = await pump(tester, companion: false);
    await connect(tester, link);
    expect(titles(c), isEmpty);
  });

  testWidgets('says nothing when the rig has a recent sync', (tester) async {
    final (c, link, _) = await pump(tester, synced: true);
    await connect(tester, link);
    expect(titles(c), isEmpty);
  });

  testWidgets('Send pushes the clock and fills the site from the lookup', (
    tester,
  ) async {
    final (c, link, rig) = await pump(tester);
    await connect(tester, link);
    c.read(simpleMessagesProvider).single.actions.last.onPressed();
    await tester.pump(const Duration(seconds: 4));
    await tester.pumpAndSettle();

    expect(rig.pushed, hasLength(1));
    final site = c.read(siteSettingsProvider);
    expect(site.latitudeDeg, closeTo(51.477833, 1e-6));
    expect(site.longitudeDeg, closeTo(-0.0015, 1e-6));
    // The question is answered: its pill is replaced by the result pill.
    expect(titles(c), isNot(contains(question)));
    expect(titles(c), isNotEmpty);
  });

  testWidgets('Not now holds the offer for the session', (tester) async {
    final (c, link, _) = await pump(tester);
    await connect(tester, link);
    c.read(simpleMessagesProvider).single.actions.first.onPressed();
    await tester.pump();
    expect(titles(c), isEmpty);

    // A reconnect in the same session does not ask again...
    link.add(WsConnectionState.reconnecting);
    await tester.pump();
    await connect(tester, link);
    expect(titles(c), isEmpty);

    // ...and the next session (a fresh container) does.
    await tester.pumpWidget(const SizedBox());
    c.dispose();
    await tester.pump();
    final (c2, link2, _) = await pump(tester);
    await connect(tester, link2);
    expect(titles(c2), [question]);
  });

  testWidgets('asks in Brazilian Portuguese', (tester) async {
    final (c, link, _) = await pump(tester, locale: const Locale('pt', 'BR'));
    await connect(tester, link);
    final pill = c.read(simpleMessagesProvider).single;
    expect(
      pill.title,
      'O equipamento ainda não tem hora nem local. Enviar a localização e a hora deste celular?',
    );
    expect(pill.actions.map((a) => a.label), ['Agora não', 'Enviar']);
  });
}
