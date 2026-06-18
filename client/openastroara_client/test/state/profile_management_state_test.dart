import 'dart:async';

import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/models/profile_list.dart';
import 'package:openastroara/models/profile_meta.dart';
import 'package:openastroara/models/server.dart';
import 'package:openastroara/services/profile_api.dart';
import 'package:openastroara/state/profile_management_state.dart';

/// In-memory ProfileApi double for the notifier — records mutation calls, can
/// hold a mutation in-flight (gate) to exercise the busy guard, and can inject a
/// mutation error to exercise error propagation + refresh-on-failure.
class _FakeApi extends ProfileApi {
  _FakeApi() : super(const AraServer(hostname: 'test', port: 1));

  List<ProfileMeta> profiles = const [ProfileMeta(id: 'id-1', name: 'A')];
  String? activeId = 'id-1';
  final List<String> selectCalls = [];
  Object? selectError;
  Completer<void>? gate; // when set, selectProfile awaits it (held in-flight)

  @override
  Future<ProfileList> listProfiles() async =>
      ProfileList(activeId: activeId, profiles: List.of(profiles));

  @override
  Future<void> selectProfile(String id) async {
    selectCalls.add(id);
    if (gate != null) await gate!.future;
    if (selectError != null) throw selectError!;
    activeId = id;
  }

  @override
  Future<void> renameProfile(String id, String name) async {}
  @override
  Future<void> deleteProfile(String id) async {}
}

ProviderContainer _container(_FakeApi api) =>
    ProviderContainer(overrides: [profileApiProvider.overrideWithValue(api)]);

void main() {
  test('build loads the list from the active api', () async {
    final api = _FakeApi();
    final c = _container(api);
    addTearDown(c.dispose);
    final list = await c.read(profileManagementProvider.future);
    expect(list.profiles, hasLength(1));
    expect(list.active?.name, 'A');
  });

  test('build surfaces AsyncError when no server is connected', () async {
    final c = ProviderContainer(
        overrides: [profileApiProvider.overrideWithValue(null)]);
    addTearDown(c.dispose);
    await expectLater(
        c.read(profileManagementProvider.future), throwsA(isA<StateError>()));
  });

  test('select calls selectProfile then refreshes the active id', () async {
    final api = _FakeApi()
      ..profiles = const [
        ProfileMeta(id: 'id-1', name: 'A'),
        ProfileMeta(id: 'id-2', name: 'B'),
      ];
    final c = _container(api);
    addTearDown(c.dispose);
    await c.read(profileManagementProvider.future);

    await c.read(profileManagementProvider.notifier).select('id-2');
    expect(api.selectCalls, ['id-2']);
    expect(c.read(profileManagementProvider).value?.activeId, 'id-2');
  });

  test('busy guard rejects an overlapping mutation', () async {
    final api = _FakeApi()..gate = Completer<void>();
    final c = _container(api);
    addTearDown(c.dispose);
    await c.read(profileManagementProvider.future);
    final notifier = c.read(profileManagementProvider.notifier);

    final first = notifier.select('id-1'); // held in-flight by the gate
    await expectLater(notifier.select('id-1'), throwsA(isA<StateError>()));
    api.gate!.complete();
    await first;
  });

  test('a mutation error propagates and the list still refreshes', () async {
    final api = _FakeApi()..selectError = StateError('boom');
    final c = _container(api);
    addTearDown(c.dispose);
    await c.read(profileManagementProvider.future);
    final notifier = c.read(profileManagementProvider.notifier);

    await expectLater(notifier.select('id-1'), throwsA(isA<StateError>()));
    // Not stranded on loading — the finally reconciled the list with the server.
    expect(c.read(profileManagementProvider).hasValue, isTrue);
  });
}
