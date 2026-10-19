import 'dart:convert';
import 'dart:typed_data';

import 'package:dio/dio.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/models/server.dart';
import 'package:openastroara/services/custom_targets_service.dart';
import 'package:openastroara/services/profile_api.dart';
import 'package:openastroara/models/profile_list.dart';
import 'package:openastroara/state/profile_management_state.dart';
import 'package:openastroara/state/saved_server_state.dart';
import 'package:openastroara/state/sky_atlas/custom_targets_state.dart';

/// In-memory scoped mirror (the real one needs a disk).
class _MemoryTargets extends CustomTargetsService {
  _MemoryTargets({Map<String, List<CustomTarget>>? seed, Map<String, Set<String>>? base, this.last})
      : scopes = {for (final e in (seed ?? {}).entries) e.key: List.of(e.value)},
        bases = {for (final e in (base ?? {}).entries) e.key: Set.of(e.value)},
        super(supportDir: () async => throw StateError('no disk'));
  final Map<String, List<CustomTarget>> scopes;
  final Map<String, Set<String>> bases;
  String? last;

  List<CustomTarget> items(String scope) => scopes.putIfAbsent(scope, () => []);
  String _key(String? scope) => scope ?? last ?? 'local';

  @override
  Future<ScopedTargets> loadScope(String? scope) async =>
      ScopedTargets(List.of(items(_key(scope))), Set.of(bases[_key(scope)] ?? const {}));

  @override
  Future<List<CustomTarget>> load({String? scope}) async => List.of(items(_key(scope)));

  @override
  Future<List<CustomTarget>> remember(CustomTarget target, {String? scope}) async {
    final k = _key(scope);
    last = k;
    items(k).removeWhere((t) => CustomTargetsService.nameKey(t) == CustomTargetsService.nameKey(target));
    items(k).insert(0, target);
    bases[k]?.remove(CustomTargetsService.nameKey(target));
    return List.of(items(k));
  }

  @override
  Future<List<CustomTarget>> forget(CustomTarget target, {String? scope}) async {
    final k = _key(scope);
    last = k;
    items(k).removeWhere((t) => CustomTargetsService.nameKey(t) == CustomTargetsService.nameKey(target));
    return List.of(items(k));
  }

  @override
  Future<List<CustomTarget>> commitSync(String scope, List<CustomTarget> synced) async {
    last = scope;
    scopes[scope] = List.of(synced);
    bases[scope] = {for (final t in synced) CustomTargetsService.nameKey(t)};
    return List.of(synced);
  }

  @override
  Future<List<CustomTarget>> replaceAll(List<CustomTarget> targets, {String? scope}) async {
    final k = _key(scope);
    last = k;
    scopes[k] = List.of(targets);
    return List.of(targets);
  }
}

/// A daemon profile section: GET answers the list, PUT stores and echoes it
/// (canonicalised like the daemon: newest first), or fails when [down].
class _ProfileAdapter implements HttpClientAdapter {
  _ProfileAdapter(this.stored, {this.down = false, this.missing = false});
  List<CustomTarget> stored;
  bool down;
  bool missing;
  int puts = 0;

  @override
  void close({bool force = false}) {}

  @override
  Future<ResponseBody> fetch(RequestOptions options, Stream<Uint8List>? requestStream,
      Future<void>? cancelFuture) async {
    if (down) {
      throw DioException.connectionError(requestOptions: options, reason: 'down');
    }
    if (missing) {
      return ResponseBody.fromString('', 404);
    }
    if (options.method == 'PUT') {
      puts++;
      final body = options.data as Map<String, dynamic>;
      stored = [for (final e in body['targets'] as List) ?CustomTarget.fromWire(e)]
        ..sort((a, b) => b.savedUtc.compareTo(a.savedUtc));
    }
    return ResponseBody.fromString(
      jsonEncode({'targets': [for (final t in stored) t.toWire()]}),
      200,
      headers: {Headers.contentTypeHeader: [Headers.jsonContentType]},
    );
  }
}

CustomTarget _t(String name, int minute,
        {String source = 'typed', List<String> aliases = const []}) =>
    CustomTarget(
      name: name,
      raDeg: 10.0 + minute,
      decDeg: 5,
      typedAsJNow: false,
      savedUtc: DateTime.utc(2026, 10, 18, 22, minute),
      aliases: aliases,
      source: source,
    );

const _server = AraServer(hostname: 'ara.test', port: 80);
const _scopeA = 'http://ara.test:80|p-a';
const _scopeB = 'http://ara.test:80|p-b';

ProviderContainer _container(_MemoryTargets mirror, _ProfileAdapter? adapter,
        {String? profileId = 'p-a'}) =>
    ProviderContainer(overrides: [
      customTargetsServiceProvider.overrideWithValue(mirror),
      activeServerProvider.overrideWithValue(adapter == null ? null : _server),
      profileManagementProvider.overrideWith(() => _FixedProfiles(profileId)),
      profileApiProvider.overrideWith((ref) => adapter == null
          ? null
          : ProfileApi(_server,
              dio: Dio(BaseOptions(baseUrl: 'http://ara.test'))..httpClientAdapter = adapter)),
    ]);

class _FixedProfiles extends ProfileManagementNotifier {
  _FixedProfiles(this.id);
  final String? id;
  @override
  Future<ProfileList> build() async =>
      ProfileList(activeId: id, profiles: const []);
}

void main() {
  test('threeWayMerge: profile wins a shared name; new entries join; forgotten entries stay gone', () {
    final merged = threeWayMergeSavedTargets(
      remote: [
        _t('AB Cas', 5, source: 'simbad', aliases: ['HIP 12235']),
        _t('New on profile', 6),
        _t('Forgotten here offline', 1),
      ],
      mirror: [_t('ab cas', 2), _t('Typed offline', 4), _t('Forgotten elsewhere', 3)],
      base: {'ab cas', 'forgotten here offline', 'forgotten elsewhere'},
    );
    expect(merged.map((t) => t.name), ['New on profile', 'AB Cas', 'Typed offline']);
    expect(merged[1].aliases, ['HIP 12235'], reason: 'the profile entry stands');
  });

  test('threeWayMerge orders equal times by name, like the daemon', () {
    final same = DateTime.utc(2026, 10, 18, 22);
    CustomTarget at(String n) => CustomTarget(name: n, raDeg: 1, decDeg: 1, typedAsJNow: false, savedUtc: same);
    final merged = threeWayMergeSavedTargets(remote: [at('zeta'), at('Alpha'), at('mid')], mirror: const [], base: const {});
    expect(merged.map((t) => t.name), ['Alpha', 'mid', 'zeta']);
  });

  test('a push re-merges against a fresh read, so another client\'s new target survives', () async {
    final mirror = _MemoryTargets(seed: {_scopeA: [_t('Mine', 1)]}, base: {_scopeA: {'mine'}});
    final adapter = _ProfileAdapter([_t('Mine', 1)]);
    final c = _container(mirror, adapter);
    addTearDown(c.dispose);
    await c.read(customTargetsProvider.future);
    // Another client adds a target after this client's build.
    adapter.stored = [_t('Theirs', 5), _t('Mine', 1)];
    await c.read(customTargetsProvider.notifier).remember(_t('New here', 6));
    expect(adapter.stored.map((t) => t.name), ['New here', 'Theirs', 'Mine']);
    expect(c.read(customTargetsProvider).value!.map((t) => t.name), ['New here', 'Theirs', 'Mine']);
  });

  test('a target re-added here after another client forgot it survives the push', () async {
    // Both synced M 31; the other client forgot it; this client re-adds it.
    final mirror = _MemoryTargets(seed: {_scopeA: [_t('Keep', 1)]}, base: {_scopeA: {'m 31', 'keep'}});
    final adapter = _ProfileAdapter([_t('Keep', 1)]);
    final c = _container(mirror, adapter);
    addTearDown(c.dispose);
    await c.read(customTargetsProvider.future);
    await c.read(customTargetsProvider.notifier).remember(_t('M 31', 9));
    expect(adapter.stored.map((t) => t.name), ['M 31', 'Keep']);
    expect(c.read(customTargetsProvider).value!.map((t) => t.name), ['M 31', 'Keep']);
  });

  test('with no server the last scope\'s mirror is the list and nothing is pushed', () async {
    final mirror = _MemoryTargets(seed: {_scopeA: [_t('Field', 1)]}, last: _scopeA);
    final c = _container(mirror, null);
    addTearDown(c.dispose);
    expect((await c.read(customTargetsProvider.future)).map((t) => t.name), ['Field']);
    await c.read(customTargetsProvider.notifier).remember(_t('Second', 2));
    expect(mirror.items(_scopeA).map((t) => t.name), ['Second', 'Field']);
  });

  test('on build the profile is merged with the scope, mirror-only entries are pushed, and the base is recorded', () async {
    final mirror = _MemoryTargets(seed: {_scopeA: [_t('Offline field', 4), _t('Shared', 1)]});
    final adapter = _ProfileAdapter([_t('Shared', 3, source: 'index'), _t('Home target', 2)]);
    final c = _container(mirror, adapter);
    addTearDown(c.dispose);
    final list = await c.read(customTargetsProvider.future);
    expect(list.map((t) => t.name), ['Offline field', 'Shared', 'Home target']);
    expect(list[1].source, 'index', reason: 'the profile wins the shared name');
    expect(adapter.puts, 1, reason: 'the merged list went up once');
    expect(mirror.items(_scopeA).map((t) => t.name), ['Offline field', 'Shared', 'Home target']);
    expect(mirror.bases[_scopeA], {'offline field', 'shared', 'home target'});
  });

  test('an identical profile list is not pushed again', () async {
    final same = [_t('A', 2), _t('B', 1)];
    final mirror = _MemoryTargets(seed: {_scopeA: same}, base: {_scopeA: {'a', 'b'}});
    final adapter = _ProfileAdapter(List.of(same));
    final c = _container(mirror, adapter);
    addTearDown(c.dispose);
    await c.read(customTargetsProvider.future);
    expect(adapter.puts, 0);
  });

  test('a forget sticks: made on another client, and made here while offline', () async {
    // Another client removed M 42 from the profile after this one synced it.
    final mirror = _MemoryTargets(seed: {_scopeA: [_t('M 42', 2), _t('Keep', 1)]}, base: {_scopeA: {'m 42', 'keep'}});
    final adapter = _ProfileAdapter([_t('Keep', 1)]);
    final c = _container(mirror, adapter);
    addTearDown(c.dispose);
    expect((await c.read(customTargetsProvider.future)).map((t) => t.name), ['Keep']);
    expect(adapter.stored.map((t) => t.name), ['Keep'], reason: 'not pushed back');

    // This client forgot M 42 while offline (mirror lacks it, base has it).
    final mirror2 = _MemoryTargets(seed: {_scopeA: [_t('Keep', 1)]}, base: {_scopeA: {'m 42', 'keep'}});
    final adapter2 = _ProfileAdapter([_t('M 42', 2), _t('Keep', 1)]);
    final c2 = _container(mirror2, adapter2);
    addTearDown(c2.dispose);
    expect((await c2.read(customTargetsProvider.future)).map((t) => t.name), ['Keep']);
    expect(adapter2.stored.map((t) => t.name), ['Keep'], reason: 'the deletion reached the profile');
  });

  test('remember and forget write the whole list through the profile and record the base', () async {
    final mirror = _MemoryTargets();
    final adapter = _ProfileAdapter([]);
    final c = _container(mirror, adapter);
    addTearDown(c.dispose);
    await c.read(customTargetsProvider.future);
    final n = c.read(customTargetsProvider.notifier);
    await n.remember(_t('AB Cas', 1, source: 'simbad', aliases: ['HIP 12235']));
    await n.remember(_t('Field', 2));
    expect(adapter.stored.map((t) => t.name), ['Field', 'AB Cas']);
    expect(adapter.stored.last.aliases, ['HIP 12235']);
    await n.forget(_t('AB Cas', 1));
    expect(adapter.stored.map((t) => t.name), ['Field']);
    expect(c.read(customTargetsProvider).value!.map((t) => t.name), ['Field']);
    expect(mirror.bases[_scopeA], {'field'});
  });

  test('profiles and servers keep their own lists', () async {
    final mirror = _MemoryTargets(seed: {_scopeA: [_t('A only', 1)]}, base: {_scopeA: {'a only'}});
    final adapterB = _ProfileAdapter([_t('B only', 2)]);
    final c = _container(mirror, adapterB, profileId: 'p-b');
    addTearDown(c.dispose);
    final list = await c.read(customTargetsProvider.future);
    expect(list.map((t) => t.name), ['B only'], reason: "profile A's targets never reach profile B");
    expect(adapterB.puts, 0);
    await c.read(customTargetsProvider.notifier).remember(_t('New on B', 3));
    expect(adapterB.stored.map((t) => t.name), ['New on B', 'B only']);
    expect(mirror.items(_scopeA).map((t) => t.name), ['A only'], reason: "scope A untouched");
    expect(mirror.items(_scopeB).map((t) => t.name), ['New on B', 'B only']);
  });

  test('a daemon that is down, older, or refusing one entry leaves the mirror in charge', () async {
    final mirror = _MemoryTargets(seed: {_scopeA: [_t('Field', 1)]});
    final down = _ProfileAdapter([], down: true);
    final c = _container(mirror, down);
    addTearDown(c.dispose);
    expect((await c.read(customTargetsProvider.future)).map((t) => t.name), ['Field']);
    await c.read(customTargetsProvider.notifier).remember(_t('Later', 2));
    expect(mirror.items(_scopeA).map((t) => t.name), ['Later', 'Field'], reason: 'kept locally for the next connect');

    final old = _ProfileAdapter([], missing: true);
    final c2 = _container(_MemoryTargets(seed: {_scopeA: [_t('Field', 1)]}), old);
    addTearDown(c2.dispose);
    expect((await c2.read(customTargetsProvider.future)).map((t) => t.name), ['Field']);

    // An entry the daemon would refuse (typed before the rules) is left out
    // of the push, so the rest still syncs.
    final bad = CustomTarget(name: 'x' * 200, raDeg: 1, decDeg: 1, typedAsJNow: false, savedUtc: DateTime.utc(2026, 10, 18, 22, 9));
    final adapter = _ProfileAdapter([]);
    final c3 = _container(_MemoryTargets(seed: {_scopeA: [bad, _t('Good', 1)]}), adapter);
    addTearDown(c3.dispose);
    await c3.read(customTargetsProvider.future);
    expect(adapter.stored.map((t) => t.name), ['Good']);
  });
}
