import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/models/profile_list.dart';
import 'package:openastroara/models/profile_meta.dart';
import 'package:openastroara/models/server.dart';
import 'package:openastroara/services/profile_api.dart';
import 'package:openastroara/state/launch_gate_state.dart';
import 'package:openastroara/state/profile_management_state.dart';
import 'package:openastroara/state/saved_server_state.dart';

class _FakeApi extends ProfileApi {
  _FakeApi({this.activeId = 'id-1', this.profiles = const [ProfileMeta(id: 'id-1', name: 'Backyard')]})
      : super(const AraServer(hostname: 'test', port: 1));

  String? activeId;
  List<ProfileMeta> profiles;
  Object? error;

  @override
  Future<ProfileList> listProfiles() async {
    if (error != null) throw error!;
    return ProfileList(activeId: activeId, profiles: profiles);
  }
}

/// Riverpod 3 settles async builds on later microtasks.
Future<void> settle() async {
  for (var i = 0; i < 5; i++) {
    await Future<void>.delayed(Duration.zero);
  }
}

ProviderContainer _container(_FakeApi api) {
  final c = ProviderContainer(overrides: [profileApiProvider.overrideWithValue(api)]);
  addTearDown(c.dispose);
  c.listen(launchAutoEnterProvider, (_, _) {});
  return c;
}

void main() {
  test('ALP-4890: a cold start with an active profile goes straight in', () async {
    final c = _container(_FakeApi());
    await settle();
    expect(c.read(profileGatePassedProvider), isTrue);
  });

  test('no active profile, no profiles, or an unreachable rig: the profile box stays', () async {
    for (final api in [
      _FakeApi(activeId: null),
      _FakeApi(activeId: 'gone'),
      _FakeApi(activeId: null, profiles: const []),
      _FakeApi()..error = StateError('rig off'),
    ]) {
      final c = _container(api);
      await settle();
      expect(c.read(profileGatePassedProvider), isFalse);
    }
  });

  test('offline planning or the server chooser is not skipped', () async {
    final offline = _container(_FakeApi());
    offline.read(offlineModeProvider.notifier).enter();
    final chooser = _container(_FakeApi());
    chooser.read(serverChooserRequestedProvider.notifier).request();
    await settle();
    expect(offline.read(profileGatePassedProvider), isFalse);
    expect(chooser.read(profileGatePassedProvider), isFalse);
  });

  test('once in, Launchpad shows the box again: only the first entry is automatic', () async {
    final c = _container(_FakeApi());
    await settle();
    c.read(profileGatePassedProvider.notifier).reset();
    c.invalidate(profileManagementProvider);
    await settle();
    expect(c.read(profileGatePassedProvider), isFalse);
  });

  test('a rig that comes back after a failed first load still goes straight in', () async {
    final api = _FakeApi()..error = StateError('rig off');
    final c = _container(api);
    await settle();
    expect(c.read(profileGatePassedProvider), isFalse);
    api.error = null;
    c.invalidate(profileManagementProvider); // the box's Retry
    await settle();
    expect(c.read(profileGatePassedProvider), isTrue);
  });

  test('a cold start with no rig goes straight in once the rig is picked', () async {
    final api = _FakeApi();
    final c = ProviderContainer(overrides: [
      profileApiProvider.overrideWith((ref) => ref.watch(activeServerProvider) == null ? null : api),
    ]);
    addTearDown(c.dispose);
    c.listen(launchAutoEnterProvider, (_, _) {});
    await settle();
    expect(c.read(profileGatePassedProvider), isFalse, reason: 'no rig yet');
    await c.read(savedServersProvider.future);
    await c.read(savedServersProvider.notifier).add(const AraServer(hostname: 'rig', port: 5555));
    await settle();
    expect(c.read(profileGatePassedProvider), isTrue);
  });
}
