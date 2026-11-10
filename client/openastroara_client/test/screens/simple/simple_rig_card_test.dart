import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/models/server.dart';
import 'package:openastroara/screens/simple/simple_rig_card.dart';
import 'package:openastroara/services/last_rig_service.dart';
import 'package:openastroara/services/server_api.dart';
import 'package:openastroara/state/launch_gate_state.dart';
import 'package:openastroara/state/saved_server_state.dart';
import 'package:openastroara/state/server_state.dart';

const _session = AraServer(hostname: '192.0.2.24', port: 5555, mdnsName: 'Backyard rig');
const _club = AraServer(hostname: '192.0.2.61', port: 5555, mdnsName: 'Club observatory');
const _travel = AraServer(hostname: '198.51.100.7', port: 5555, mdnsName: 'Travel rig');

/// The session's rigs, without storage: [add] records the pick.
class _Saved extends SavedServersNotifier {
  _Saved(this.initial);
  final List<AraServer> initial;
  final added = <AraServer>[];
  @override
  Future<List<AraServer>> build() async => initial;
  @override
  Future<void> add(AraServer server) async {
    added.add(server);
    state = AsyncData([...state.value!.where((s) => s != server), server]);
  }
}

class _MemLastRig extends LastRigService {
  LastRig? rig;
  @override
  Future<LastRig?> load() async => rig;
  @override
  Future<void> save(LastRig rig) async => this.rig = rig;
}

/// The card on its own, with [found] on the network and [saved] this session
/// (the last one active). The handshake answers unless the rig is [down].
Future<(ProviderContainer, _Saved, List<AraServer>)> _card(WidgetTester tester,
    {List<AraServer> found = const [_session, _club],
    List<AraServer> saved = const [_travel, _club, _session],
    Set<AraServer> down = const {},
    List<AraServer> foundOnce = const [],
    bool offline = false}) async {
  final saves = _Saved(saved);
  final asked = <AraServer>[];
  var scans = 0;
  final container = ProviderContainer(overrides: [
    savedServersProvider.overrideWith(() => saves),
    // [foundOnce]: those rigs answer the first scan only, then leave the network.
    discoveredServersProvider.overrideWith((ref) => Stream.fromIterable(scans++ == 0 ? [...found, ...foundOnce] : found)),
    lastRigServiceProvider.overrideWithValue(_MemLastRig()),
    serverHandshakeProvider.overrideWith((ref) async {
      final s = ref.watch(selectedServerProvider);
      if (s == null) return null;
      asked.add(s);
      if (down.contains(s)) throw StateError('no answer');
      return ServerInfo(name: s.mdnsName ?? s.hostname, version: '1', apiVersion: '1');
    }),
  ]);
  addTearDown(container.dispose);
  await container.read(savedServersProvider.future);
  container.read(profileGatePassedProvider.notifier).pass();
  if (offline) container.read(offlineModeProvider.notifier).enter();
  await tester.pumpWidget(UncontrolledProviderScope(
    container: container,
    child: const MaterialApp(home: Scaffold(body: Center(child: SimpleRigCard()))),
  ));
  await tester.pump();
  await tester.pump();
  return (container, saves, asked);
}

void main() {
  testWidgets('#1559: the session rig comes first, then every other rig once, found or saved', (tester) async {
    await _card(tester);
    expect(find.text('Connect to your rig'), findsOneWidget);
    expect(find.textContaining('The link to Backyard rig dropped'), findsOneWidget);
    expect(find.byKey(const Key('simple-rig-card-row-192.0.2.24:5555')), findsOneWidget, reason: 'found and saved: one row');
    expect(find.byKey(const Key('simple-rig-card-row-192.0.2.61:5555')), findsOneWidget, reason: 'found and saved: one row');
    expect(find.byKey(const Key('simple-rig-card-reconnect')), findsOneWidget);
    expect(find.descendant(of: find.byKey(const Key('simple-rig-card-row-192.0.2.61:5555')), matching: find.textContaining('Found')),
        findsOneWidget);
    expect(find.descendant(of: find.byKey(const Key('simple-rig-card-row-198.51.100.7:5555')), matching: find.textContaining('Saved, not seen now')),
        findsOneWidget);
    expect(tester.getTopLeft(find.byKey(const Key('simple-rig-card-row-192.0.2.24:5555'))).dy,
        lessThan(tester.getTopLeft(find.byKey(const Key('simple-rig-card-row-192.0.2.61:5555'))).dy));
  });

  testWidgets('#1559: Reconnect on the session rig keeps the launch gate open', (tester) async {
    final (c, saves, asked) = await _card(tester);
    await tester.tap(find.byKey(const Key('simple-rig-card-reconnect')));
    await tester.pump();
    await tester.pump();
    expect(asked, [_session]);
    expect(saves.added, [_session]);
    expect(c.read(profileGatePassedProvider), isTrue, reason: 'the same rig comes back in place');
  });

  testWidgets('#1559: Connect on another rig makes it active and opens its profile box', (tester) async {
    final (c, saves, asked) = await _card(tester);
    await tester.tap(find.byKey(const Key('simple-rig-card-connect-192.0.2.61:5555')));
    await tester.pump();
    await tester.pump();
    expect(asked, [_club]);
    expect(c.read(activeServerProvider), _club);
    expect(c.read(profileGatePassedProvider), isFalse, reason: 'its profile box, as Change rig');
  });

  testWidgets('#1559: Add by address asks the typed address, port 5555 unless given', (tester) async {
    final (_, _, asked) = await _card(tester);
    await tester.tap(find.byKey(const Key('simple-rig-card-address-open')));
    await tester.pump();
    await tester.enterText(find.byKey(const Key('simple-rig-card-address')), 'astro-pi.local');
    await tester.tap(find.byKey(const Key('simple-rig-card-address-connect')));
    await tester.pump();
    await tester.pump();
    expect(asked, [const AraServer(hostname: 'astro-pi.local', port: 5555)]);
    expect(parseRigAddress('192.0.2.9:6000'), const AraServer(hostname: '192.0.2.9', port: 6000));
    expect(parseRigAddress('host:99999'), isNull);
  });

  testWidgets('#1559: a rig that does not answer says so and keeps the user where they are', (tester) async {
    final (c, saves, _) = await _card(tester, down: {_club});
    await tester.tap(find.byKey(const Key('simple-rig-card-connect-192.0.2.61:5555')));
    await tester.pump();
    await tester.pump();
    expect(find.textContaining('No rig answered at 192.0.2.61:5555'), findsOneWidget);
    expect(saves.added, isEmpty);
    expect(c.read(profileGatePassedProvider), isTrue);
  });

  testWidgets('#1559: the no-answer line is announced to screen readers', (tester) async {
    await _card(tester, down: {_club});
    await tester.tap(find.byKey(const Key('simple-rig-card-connect-192.0.2.61:5555')));
    await tester.pump();
    await tester.pump();
    final semantics = tester.getSemantics(find.byKey(const Key('simple-rig-card-error')));
    expect(semantics.flagsCollection.isLiveRegion, isTrue);
  });

  testWidgets('#1559: an address that cannot be read says so, and nothing is asked', (tester) async {
    final (_, _, asked) = await _card(tester);
    await tester.tap(find.byKey(const Key('simple-rig-card-address-open')));
    await tester.pump();
    await tester.enterText(find.byKey(const Key('simple-rig-card-address')), 'pi:99999');
    await tester.tap(find.byKey(const Key('simple-rig-card-address-connect')));
    await tester.pump();
    expect(asked, isEmpty);
    expect(find.textContaining("Type the rig's name or address"), findsOneWidget);
    await tester.enterText(find.byKey(const Key('simple-rig-card-address')), 'pi.local');
    await tester.pump();
    expect(find.textContaining("Type the rig's name or address"), findsNothing, reason: 'typing clears it');
  });

  testWidgets('#1559: a rig not seen for two rescans is no longer Found', (tester) async {
    const gone = AraServer(hostname: '192.0.2.77', port: 5555, mdnsName: 'Borrowed rig');
    await _card(tester, foundOnce: const [gone, _travel]);
    final goneRow = find.byKey(const Key('simple-rig-card-row-192.0.2.77:5555'));
    final travelRow = find.byKey(const Key('simple-rig-card-row-198.51.100.7:5555'));
    expect(find.descendant(of: travelRow, matching: find.textContaining('Found')), findsOneWidget);
    for (var i = 0; i < 3; i++) {
      await tester.pump(const Duration(seconds: 4)); // one rescan each
      await tester.pump();
    }
    expect(goneRow, findsNothing, reason: 'found only, and gone: no row left');
    expect(find.descendant(of: travelRow, matching: find.textContaining('Saved, not seen now')), findsOneWidget,
        reason: 'saved: back to its saved label');
  });

  testWidgets('#1559: offline planning shows the same card to go online', (tester) async {
    final (c, _, _) = await _card(tester, offline: true);
    expect(find.text('Go online'), findsOneWidget);
    expect(find.byKey(const Key('simple-rig-card-reconnect')), findsNothing, reason: 'no session rig offline');
    await tester.tap(find.byKey(const Key('simple-rig-card-connect-192.0.2.24:5555')));
    await tester.pump();
    await tester.pump();
    expect(c.read(offlineModeProvider), isFalse);
    expect(c.read(profileGatePassedProvider), isFalse, reason: 'the rig\'s profile box next');
  });
}
