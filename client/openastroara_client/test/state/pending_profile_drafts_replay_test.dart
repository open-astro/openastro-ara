import 'dart:io';

import 'package:dio/dio.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/models/profile_draft.dart' show ProfileDraft;
import 'package:openastroara/models/profile_list.dart';
import 'package:openastroara/models/profile_meta.dart';
import 'package:openastroara/models/server.dart';
import 'package:openastroara/services/pending_profile_draft_service.dart';
import 'package:openastroara/services/profile_api.dart';
import 'package:openastroara/state/pending_profile_drafts_state.dart';
import 'package:openastroara/state/settings/site_settings_state.dart';
import '../support/fake_section_rig.dart';

/// A ProfileApi double for the replay: records creates, can fail one section.
/// The sections themselves reach [rig], which refuses a PUT without the
/// loaded tag (428) as the daemon does.
class _FakeProfileApi extends ProfileApi {
  _FakeProfileApi() : this._(FakeSectionRig({...rigDefaults, 'storage': rigStorage}));
  _FakeProfileApi._(this.rig)
    : super(
        const AraServer(hostname: 'test', port: 1),
        dio: Dio(BaseOptions(baseUrl: 'http://test:1'))..httpClientAdapter = rig,
      );
  final FakeSectionRig rig;
  final created = <String>[];
  bool failSite = false; // in transit: no response at all
  bool rejectSite = false; // the daemon answers 422
  bool busy = false; // a run is active: create answers 409
  bool siteSavedMeanwhile = false; // another write lands between GET and PUT
  int n = 0;

  @override
  Future<ProfileMeta> createProfile(String name) async {
    if (busy) {
      throw DioException(
        requestOptions: RequestOptions(path: '/profiles'),
        type: DioExceptionType.badResponse,
        response: Response(
          requestOptions: RequestOptions(path: '/profiles'),
          statusCode: 409,
        ),
      );
    }
    created.add(name);
    activeId = 'profile-${++n}';
    known.add(activeId!);
    return ProfileMeta(id: activeId!, name: name);
  }

  // The daemon's profile list: which one is active, and which exist.
  String? activeId;
  final known = <String>[];
  final selected = <String>[];
  @override
  Future<ProfileList> listProfiles() async => ProfileList(
    activeId: activeId,
    profiles: [for (final id in known) ProfileMeta(id: id, name: id)],
  );
  @override
  Future<List<ProfileDeviceMessage>> selectProfile(String id) async {
    selected.add(id);
    activeId = id;
    return const [];
  }

  @override
  Future<({SiteSettings value, String? etag})> putSiteSection(
    SiteSettings v, {
    String? ifMatch,
  }) async {
    if (failSite) {
      throw DioException(
        requestOptions: RequestOptions(path: '/api/v1/profile/site'),
        type: DioExceptionType.connectionError,
      );
    }
    if (rejectSite) {
      throw DioException(
        requestOptions: RequestOptions(path: '/api/v1/profile/site'),
        type: DioExceptionType.badResponse,
        response: Response(
          requestOptions: RequestOptions(path: '/api/v1/profile/site'),
          statusCode: 422,
        ),
      );
    }
    if (siteSavedMeanwhile) {
      siteSavedMeanwhile = false;
      rig.savedElsewhere('site');
    }
    return super.putSiteSection(v, ifMatch: ifMatch);
  }
}

void main() {
  late Directory tmp;
  late PendingProfileDraftService store;

  setUp(() async {
    tmp = await Directory.systemTemp.createTemp('pending_replay');
    store = PendingProfileDraftService(supportDir: () async => tmp);
  });
  tearDown(() => tmp.delete(recursive: true));

  Future<void> pend(String name, {String? serverKey, String? savedProfileId}) =>
      store.save(
        PendingProfileDraft(
          id: store.newId(),
          name: name,
          savedUtc: DateTime.now().toUtc(),
          draft: ProfileDraft()
            ..profileName = name
            ..savedProfileId = savedProfileId,
          finalSave: true,
          serverKey: serverKey,
        ),
      );

  test(
    '#1137: every pending draft is created on the daemon and then forgotten',
    () async {
      await pend('Dark site rig');
      await pend('Backyard');
      final api = _FakeProfileApi();

      final (applied, failed, held, _) = await replayPendingProfileDrafts(
        store,
        api,
        serverKey: 'rig-a:5555',
      );

      expect((applied, failed, held), (2, 0, 0));
      expect(api.created, containsAll(['Dark site rig', 'Backyard']));
      expect(await store.loadAll(), isEmpty);
    },
  );

  test('a replayed draft sends every section the tag of the copy it loaded', () async {
    await pend('Dark site rig');
    final api = _FakeProfileApi();

    final (applied, _, _, _) = await replayPendingProfileDrafts(
      store,
      api,
      serverKey: 'rig-a:5555',
    );

    expect(applied, 1);
    expect(api.rig.puts, hasLength(10), reason: 'every section the wizard writes');
    expect(api.rig.ifMatches, isNot(contains(null)));
    for (final p in api.rig.puts) {
      expect(p.ifMatch, '"${p.section}-v1"', reason: p.section);
    }
  });

  test('#1137: a draft whose sections failed IN TRANSIT stays pending with its created profile id', () async {
    await pend('Flaky');
    final api = _FakeProfileApi()..failSite = true;

    final (applied, failed, held, _) = await replayPendingProfileDrafts(
      store,
      api,
      serverKey: 'rig-a:5555',
    );

    expect((applied, failed, held), (0, 1, 0));
    final left = await store.loadAll();
    expect(left.single.name, 'Flaky');
    expect(
      left.single.draft.savedProfileId,
      'profile-1',
      reason: 'the retry re-applies onto the profile already created instead of orphaning another',
    );

    api.failSite = false;
    final second = await replayPendingProfileDrafts(
      store,
      api,
      serverKey: 'rig-a:5555',
    );
    expect(second, (1, 0, 0, 0));
    expect(api.created, ['Flaky'], reason: 'no second create on the retry');
    expect(await store.loadAll(), isEmpty);
  });

  test(
    '#1327: a draft saved against another rig is held, not applied here',
    () async {
      await pend('For rig B', serverKey: 'rig-b:5555');
      await pend('Anywhere');
      final api = _FakeProfileApi();

      final (applied, failed, held, _) = await replayPendingProfileDrafts(
        store,
        api,
        serverKey: 'rig-a:5555',
      );

      expect((applied, failed, held), (1, 0, 1));
      expect(api.created, ['Anywhere']);
      expect(
        (await store.loadAll()).single.name,
        'For rig B',
        reason: 'waits for rig B',
      );
    },
  );

  test('#1327: a half-created profile that is no longer active is re-selected before its sections land', () async {
    final api = _FakeProfileApi()
      ..known.addAll(['profile-old', 'other'])
      ..activeId = 'other';
    await pend(
      'Resumed',
      serverKey: 'rig-a:5555',
      savedProfileId: 'profile-old',
    );

    final result = await replayPendingProfileDrafts(
      store,
      api,
      serverKey: 'rig-a:5555',
    );

    expect(result, (1, 0, 0, 0));
    expect(api.created, isEmpty, reason: 'no second profile');
    expect(api.selected, [
      'profile-old',
    ], reason: 'the sections must not land on "other"');
  });

  test('#1327: a half-created profile that was deleted meanwhile starts a fresh one', () async {
    final api = _FakeProfileApi()
      ..known.add('other')
      ..activeId = 'other';
    await pend(
      'Vanished',
      serverKey: 'rig-a:5555',
      savedProfileId: 'profile-gone',
    );

    final result = await replayPendingProfileDrafts(
      store,
      api,
      serverKey: 'rig-a:5555',
    );

    expect(result, (1, 0, 0, 0));
    expect(api.created, ['Vanished']);
    expect(api.selected, isEmpty);
  });

  test('a draft that reaches the rig during a run stays pending and lands afterwards', () async {
    await pend('Dark site');
    final api = _FakeProfileApi()..busy = true;

    final first = await replayPendingProfileDrafts(
      store,
      api,
      serverKey: 'rig-a:5555',
    );

    expect(first, (0, 1, 0, 0));
    expect((await store.loadAll()).single.name, 'Dark site');

    api.busy = false;
    final second = await replayPendingProfileDrafts(
      store,
      api,
      serverKey: 'rig-a:5555',
    );
    expect(second, (1, 0, 0, 0));
    expect(await store.loadAll(), isEmpty);
  });

  test('#1327: a draft the daemon rejects is dropped, not retried on every reconnect', () async {
    await pend('Bad value');
    final api = _FakeProfileApi()..rejectSite = true;

    final first = await replayPendingProfileDrafts(
      store,
      api,
      serverKey: 'rig-a:5555',
    );

    expect(first, (0, 0, 0, 1));
    expect(api.created, [
      'Bad value',
    ], reason: 'the profile it did create stays on the rig');
    expect(await store.loadAll(), isEmpty, reason: 'nothing left to retry');

    // The user moves the rig to their real profile; a reconnect must not swap it back.
    api
      ..known.add('real')
      ..activeId = 'real';
    final second = await replayPendingProfileDrafts(
      store,
      api,
      serverKey: 'rig-a:5555',
    );
    expect(second, (0, 0, 0, 0));
    expect(
      api.selected,
      isEmpty,
      reason: 'no re-select of the rejected profile',
    );
    expect(api.activeId, 'real');
  });

  test('a replayed draft on a rig that sends no version stays pending and '
      'lands once the rig sends one', () async {
    await pend('Old rig');
    final api = _FakeProfileApi();
    api.rig.sendsTags = false;

    final first = await replayPendingProfileDrafts(
      store,
      api,
      serverKey: 'rig-a:5555',
    );

    expect(first, (0, 1, 0, 0), reason: 'kept, not rejected');
    expect(api.rig.puts, isEmpty, reason: 'no untagged write');
    final left = (await store.loadAll()).single;
    expect(left.name, 'Old rig', reason: 'the wizard input survives');
    expect(left.draft.savedProfileId, 'profile-1');

    api.rig.sendsTags = true;
    final second = await replayPendingProfileDrafts(
      store,
      api,
      serverKey: 'rig-a:5555',
    );
    expect(second, (1, 0, 0, 0));
    expect(api.created, ['Old rig'], reason: 'no second create');
    expect(await store.loadAll(), isEmpty);
  });

  test('a replayed draft whose section was saved meanwhile (412) stays '
      'pending and lands on the next connect', () async {
    await pend('Raced');
    final api = _FakeProfileApi()..siteSavedMeanwhile = true;

    final first = await replayPendingProfileDrafts(
      store,
      api,
      serverKey: 'rig-a:5555',
    );

    expect(first, (0, 1, 0, 0), reason: 'kept, not rejected');
    expect((await store.loadAll()).single.name, 'Raced');

    final second = await replayPendingProfileDrafts(
      store,
      api,
      serverKey: 'rig-a:5555',
    );
    expect(second, (1, 0, 0, 0));
    expect(api.created, ['Raced'], reason: 'no second create');
    expect(await store.loadAll(), isEmpty);
  });

  test('#1327: a draft that half-landed on this rig is pinned here, so another rig holds it', () async {
    await pend('Roaming'); // saved fully offline: no server recorded
    final api = _FakeProfileApi()..failSite = true;

    final first = await replayPendingProfileDrafts(
      store,
      api,
      serverKey: 'rig-a:5555',
    );
    expect(first, (0, 1, 0, 0));
    final left = (await store.loadAll()).single;
    expect(left.draft.savedProfileId, 'profile-1');
    expect(
      left.serverKey,
      'rig-a:5555',
      reason: 'the profile now lives on rig A',
    );

    final rigB = _FakeProfileApi();
    final onB = await replayPendingProfileDrafts(
      store,
      rigB,
      serverKey: 'rig-b:5555',
    );
    expect(onB, (0, 0, 1, 0), reason: 'held for rig A');
    expect(rigB.created, isEmpty, reason: 'no orphan on A, no duplicate on B');

    api.failSite = false;
    final backOnA = await replayPendingProfileDrafts(
      store,
      api,
      serverKey: 'rig-a:5555',
    );
    expect(backOnA, (1, 0, 0, 0));
    expect(api.created, ['Roaming'], reason: 'the same profile is finished');
  });
}
