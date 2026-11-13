import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/models/profile_list.dart';
import 'package:openastroara/models/server.dart';
import 'package:openastroara/state/profile_management_state.dart';
import 'package:openastroara/state/saved_server_state.dart';
import 'package:openastroara/state/settings/loaded_section.dart';
import 'package:openastroara/state/settings/migrated_sections.dart';
import 'package:openastroara/state/settings/storage_settings_state.dart';

import '../../support/storage_fixture.dart';

class _Server extends Notifier<AraServer?> {
  @override
  AraServer? build() => const AraServer(hostname: 'a.local', port: 1);
  void set(AraServer? s) => state = s;
}

final _serverProvider = NotifierProvider<_Server, AraServer?>(_Server.new);

class _Profiles extends ProfileManagementNotifier {
  @override
  Future<ProfileList> build() async =>
      const ProfileList(activeId: 'p1', profiles: []);
  void activate(String id) =>
      state = AsyncData(ProfileList(activeId: id, profiles: const []));
}

void main() {
  late ProviderContainer c;
  late FakeStorageApi api;
  StorageSettingsNotifier n() => c.read(storageSettingsProvider.notifier);
  LoadedSection<StorageSettings> s() => c.read(storageSettingsProvider);

  setUp(() {
    api = FakeStorageApi();
    c = ProviderContainer(
      overrides: [
        activeServerProvider.overrideWith((ref) => ref.watch(_serverProvider)),
        profileManagementProvider.overrideWith(_Profiles.new),
      ],
    );
    addTearDown(c.dispose);
  });

  group('Storage load-before-save (ADR 0015 rules 1-3)', () {
    test('nothing is loaded until the rig answers; edits are ignored', () {
      expect(s().source, SectionSource.notLoaded);
      expect(s().value, isNull);
      n().setSaveDirectory('/mnt/usb');
      expect(s().value, isNull);
    });

    test(
      'Save with the load failing twice refuses and writes nothing',
      () async {
        api.failGet = Exception('rig unreachable');
        await n().load(api);
        expect(s().lastFailure, isNotNull);
        await expectLater(
          n().save(api),
          throwsA(isA<SectionNotLoadedException>()),
        );
        expect(api.gets, 2, reason: 'one retry of the load');
        expect(api.puts, isEmpty);
      },
    );

    test(
      'Save after a failed load retries the load once, then saves',
      () async {
        api.failGet = Exception('rig unreachable');
        await n().load(api);
        api.failGet = null;
        await n().save(api);
        expect(s().source, SectionSource.rig);
        expect(api.puts, [kTestStorage]);
      },
    );

    test('a cache-sourced section cannot save', () async {
      c.read(storageSettingsProvider); // build
      api.failGet = Exception('offline');
      const cached = LoadedSection<StorageSettings>(
        value: kTestStorage,
        source: SectionSource.cache,
      );
      expect(cached.canSave, isFalse);
      expect(const LoadedSection<StorageSettings>().canSave, isFalse);
    });

    test('Save sends the loaded ETag and keeps the new one', () async {
      await n().load(api);
      n().setSaveDirectory('/mnt/usb');
      await n().save(api);
      expect(api.ifMatches, ['"v1"']);
      expect(s().etag, '"v2"');
      expect(s().value!.saveDirectory, '/mnt/usb');
      n().setSaveDirectory('/mnt/other');
      await n().save(api);
      expect(api.ifMatches, ['"v1"', '"v2"']);
    });

    test(
      '412 keeps the edits, flags the conflict, writes and retries nothing',
      () async {
        await n().load(api);
        api.etag = '"newer"'; // another device saved
        n().setSaveDirectory('/mnt/usb');
        await expectLater(
          n().save(api),
          throwsA(isA<SectionConflictException>()),
        );
        expect(s().conflict, isTrue);
        expect(s().value!.saveDirectory, '/mnt/usb');
        expect(api.puts, isEmpty);
        expect(api.gets, 1, reason: 'no automatic reload');
      },
    );

    test(
      'Reload after a conflict replaces the edits with the rig copy',
      () async {
        await n().load(api);
        api.etag = '"newer"';
        n().setSaveDirectory('/mnt/usb');
        await expectLater(
          n().save(api),
          throwsA(isA<SectionConflictException>()),
        );
        await n().load(api);
        expect(s().conflict, isFalse);
        expect(s().value, kTestStorage);
        expect(s().etag, '"newer"');
      },
    );
  });

  group('one reset point (ADR 0015 rule 4)', () {
    setUp(() async {
      c.listen(migratedSectionsResetProvider, (_, _) {});
      c.listen(profileManagementProvider, (_, _) {});
      await c.read(profileManagementProvider.future);
      await n().load(api);
    });

    test('a different rig sends Storage back to not loaded', () async {
      expect(s().canSave, isTrue);
      c
          .read(_serverProvider.notifier)
          .set(const AraServer(hostname: 'b.local', port: 1));
      await Future<void>.delayed(Duration.zero);
      expect(s().source, SectionSource.notLoaded);
      expect(s().value, isNull);
    });

    test('a different active profile sends Storage back to not loaded', () {
      expect(s().canSave, isTrue);
      (c.read(profileManagementProvider.notifier) as _Profiles).activate('p2');
      expect(s().source, SectionSource.notLoaded);
    });

    test('the same rig and profile leave it loaded', () {
      c
          .read(_serverProvider.notifier)
          .set(const AraServer(hostname: 'a.local', port: 1));
      expect(s().canSave, isTrue);
    });
  });
}
