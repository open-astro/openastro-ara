import 'dart:async';

import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/l10n/app_localizations.dart';
import 'package:openastroara/services/time_sync_api.dart';
import 'package:openastroara/services/ws_event_stream.dart';
import 'package:openastroara/state/profile_management_state.dart';
import 'package:openastroara/state/settings/site_settings_state.dart';
import 'package:openastroara/state/simple_messages_state.dart';
import 'package:openastroara/state/simple_mode_state.dart';
import 'package:openastroara/state/time_sync_state.dart';
import 'package:openastroara/state/ws/ws_providers.dart';
import 'package:openastroara/widgets/companion_time_offer_listener.dart';
import 'package:openastroara/widgets/time_sync_on_connect.dart';

import '../support/fake_section_rig.dart';
import '../support/site_section.dart';
import '../support/settings_values.dart';

class _Rig implements TimeSyncClient {
  _Rig({required this.synced});
  final bool synced;
  final pushed = <DateTime>[];

  @override
  Future<TimeSyncState> getState() async {
    // A real request takes a turn: a read started now sees a push made meanwhile.
    await Future<void>.value();
    return TimeSyncState(
      // Like the real rig: once a clock is pushed it reads as synced.
      synced: synced || pushed.isNotEmpty,
      // Greenwich: the position the rig "holds", what the site fill reads back.
      location: const TimeSyncLocation(lat: 51.477833, lng: -0.0015, alt: 42),
    );
  }

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
    FakeSiteSection? site,
  }) async {
    final link = StreamController<WsConnectionState>();
    addTearDown(link.close);
    final rig = _Rig(synced: synced);
    final container = ProviderContainer(
      overrides: [
        // The profile's site as the rig serves it (ADR 0015), with no HTTP.
        profileApiProvider.overrideWithValue(FakeSectionRig({}).api()),
        siteSettingsProvider.overrideWith(() => site ?? FakeSiteSection()),
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
            body: TimeSyncOnConnectListener(
              child: CompanionTimeOfferListener(child: SizedBox.shrink()),
            ),
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

  testWidgets('still offers when the on-connect push lands first', (
    tester,
  ) async {
    final (c, link, rig) = await pump(tester);
    await connect(tester, link);
    // The on-connect push already set the phone clock, so a fresh read of the
    // rig says synced; the offer uses the reading taken before that push.
    expect(rig.pushed, hasLength(1));
    expect((await rig.getState()).synced, isTrue);
    expect(titles(c), [question]);
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
    final section = FakeSiteSection(
      initial: const TestSiteSettings(siteName: 'Dark site', bortleClass: 2),
    );
    final (c, link, rig) = await pump(tester, site: section);
    await connect(tester, link);
    c.read(simpleMessagesProvider).single.actions.last.onPressed();
    await tester.pump(const Duration(seconds: 4));
    await tester.pumpAndSettle();

    // The on-connect push, then Send's own.
    expect(rig.pushed, hasLength(2));
    final site = c.read(siteSettingsProvider).value!;
    expect(site.latitudeDeg, closeTo(51.477833, 1e-6));
    expect(site.longitudeDeg, closeTo(-0.0015, 1e-6));
    // Saved onto the rig's copy: the rest of it goes back unchanged.
    expect(section.puts.single.latitudeDeg, closeTo(51.477833, 1e-6));
    expect(section.puts.single.siteName, 'Dark site');
    expect(section.puts.single.bortleClass, 2);
    // The question is answered: its pill is replaced by the result pill.
    expect(titles(c), isNot(contains(question)));
    expect(titles(c), isNotEmpty);
    await tester.pump(const Duration(seconds: 11)); // the result pill leaves
  });

  testWidgets(
    'Send with the site only from the offline cache saves nothing and says why (ADR 0015)',
    (tester) async {
      // The rig does not answer for the site; the cache copy is all there is.
      final section = FakeSiteSection(initial: null);
      final (c, link, _) = await pump(tester, site: section);
      c
          .read(siteSettingsProvider.notifier)
          .seed(const TestSiteSettings(siteName: 'Cached', latitudeDeg: 10));
      await connect(tester, link);
      c.read(simpleMessagesProvider).single.actions.last.onPressed();
      await tester.pump(const Duration(seconds: 4));
      await tester.pumpAndSettle();

      expect(section.puts, isEmpty);
      expect(c.read(siteSettingsProvider).value!.latitudeDeg, 10);
      final pill = c.read(simpleMessagesProvider).last;
      expect(pill.title, "Couldn't save the site");
      expect(pill.detail, contains('Settings not loaded from the rig'));
    },
  );

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
      'O setup ainda não tem hora nem local. Enviar a localização e a hora deste celular?',
    );
    expect(pill.actions.map((a) => a.label), ['Agora não', 'Enviar']);
  });
}
