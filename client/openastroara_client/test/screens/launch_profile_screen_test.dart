import 'dart:async';

import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/models/profile_list.dart';
import 'package:openastroara/models/profile_meta.dart';
import 'package:openastroara/models/server.dart';
import 'package:openastroara/screens/launch_profile_screen.dart';
import 'package:openastroara/services/profile_api.dart';
import 'package:openastroara/state/launch_gate_state.dart';
import 'package:openastroara/state/profile_management_state.dart';

/// In-memory ProfileApi double — same shape as the one in
/// profile_management_state_test.dart, plus a select-error injector.
class _FakeApi extends ProfileApi {
  _FakeApi() : super(const AraServer(hostname: 'test', port: 1));

  List<ProfileMeta> profiles = const [
    ProfileMeta(id: 'id-1', name: 'My Backyard Rig'),
    ProfileMeta(id: 'id-2', name: 'Travel Rig'),
  ];
  String? activeId = 'id-1';
  final List<String> selectCalls = [];
  Object? selectError;

  @override
  Future<ProfileList> listProfiles() async =>
      ProfileList(activeId: activeId, profiles: List.of(profiles));

  @override
  Future<void> selectProfile(String id) async {
    selectCalls.add(id);
    if (selectError != null) throw selectError!;
    activeId = id;
  }
}

Future<ProviderContainer> _pump(WidgetTester tester, _FakeApi api) async {
  final container = ProviderContainer(
      overrides: [profileApiProvider.overrideWithValue(api)]);
  addTearDown(container.dispose);
  await tester.pumpWidget(UncontrolledProviderScope(
    container: container,
    child: const MaterialApp(home: LaunchProfileScreen()),
  ));
  await tester.pumpAndSettle();
  return container;
}

void main() {
  testWidgets('shows the active profile pre-selected with an Image button',
      (tester) async {
    final container = await _pump(tester, _FakeApi());
    expect(find.text('My Backyard Rig'), findsOneWidget); // dropdown value
    expect(find.text('Image'), findsOneWidget);
    // §30.2: Add / Import always visible.
    expect(find.text('Add a Profile'), findsOneWidget);
    expect(find.text('Import Profile'), findsOneWidget);
    expect(container.read(profileGatePassedProvider), isFalse);
  });

  testWidgets('Image on the already-active profile passes the gate without a select RPC',
      (tester) async {
    final api = _FakeApi();
    final container = await _pump(tester, api);
    await tester.tap(find.text('Image'));
    await tester.pumpAndSettle();
    expect(api.selectCalls, isEmpty);
    expect(container.read(profileGatePassedProvider), isTrue);
  });

  testWidgets('picking another profile then Image selects it on the daemon first',
      (tester) async {
    final api = _FakeApi();
    final container = await _pump(tester, api);
    await tester.tap(find.text('My Backyard Rig'));
    await tester.pumpAndSettle();
    await tester.tap(find.text('Travel Rig').last);
    await tester.pumpAndSettle();
    await tester.tap(find.text('Image'));
    await tester.pumpAndSettle();
    expect(api.selectCalls, ['id-2']);
    expect(container.read(profileGatePassedProvider), isTrue);
  });

  testWidgets('a failed profile switch keeps the gate closed and shows the error',
      (tester) async {
    final api = _FakeApi()..selectError = StateError('daemon said no');
    final container = await _pump(tester, api);
    await tester.tap(find.text('My Backyard Rig'));
    await tester.pumpAndSettle();
    await tester.tap(find.text('Travel Rig').last);
    await tester.pumpAndSettle();
    await tester.tap(find.text('Image'));
    await tester.pumpAndSettle();
    expect(container.read(profileGatePassedProvider), isFalse);
    expect(find.text('daemon said no'), findsOneWidget);
  });

  testWidgets('no profiles: dropdown + Image hidden, Add / Import remain',
      (tester) async {
    final api = _FakeApi()
      ..profiles = const []
      ..activeId = null;
    await _pump(tester, api);
    expect(find.text('Image'), findsNothing);
    expect(find.byType(DropdownButtonFormField<String>), findsNothing);
    expect(find.text('Add a Profile'), findsOneWidget);
    expect(find.text('Import Profile'), findsOneWidget);
  });

  testWidgets('no server: shows the friendly error with a Retry action',
      (tester) async {
    final container = ProviderContainer(
        overrides: [profileApiProvider.overrideWithValue(null)]);
    addTearDown(container.dispose);
    await tester.pumpWidget(UncontrolledProviderScope(
      container: container,
      child: const MaterialApp(home: LaunchProfileScreen()),
    ));
    await tester.pumpAndSettle();
    expect(find.textContaining('No active server'), findsOneWidget);
    expect(find.text('Retry'), findsOneWidget);
    expect(find.text('Image'), findsNothing);
  });
}
