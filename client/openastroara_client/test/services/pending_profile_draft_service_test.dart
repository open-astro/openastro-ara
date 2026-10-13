import 'dart:io';

import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/models/profile_draft.dart';
import 'package:openastroara/services/pending_profile_draft_service.dart';

void main() {
  late Directory tmp;
  late PendingProfileDraftService store;

  setUp(() async {
    tmp = await Directory.systemTemp.createTemp('pending_profile_drafts');
    store = PendingProfileDraftService(supportDir: () async => tmp);
  });
  tearDown(() => tmp.delete(recursive: true));

  PendingProfileDraft make(String name, DateTime at) => PendingProfileDraft(
    id: store.newId(),
    name: name,
    savedUtc: at,
    draft: ProfileDraft()..profileName = name,
    finalSave: true,
  );

  test(
    '#1137: drafts persist across service instances, oldest first, and delete',
    () async {
      final b = make('B', DateTime.utc(2026, 10, 12, 22));
      final a = make('A', DateTime.utc(2026, 10, 12, 21));
      await store.save(b);
      await store.save(a);

      final again = PendingProfileDraftService(supportDir: () async => tmp);
      final loaded = await again.loadAll();
      expect(loaded.map((d) => d.name), ['A', 'B']);
      expect(loaded.first.draft.profileName, 'A');
      expect(loaded.first.finalSave, isTrue);

      await again.delete(a.id);
      await again.delete('never-existed');
      expect((await again.loadAll()).map((d) => d.name), ['B']);
    },
  );

  test('#1137: a corrupt file is skipped, not fatal', () async {
    await store.save(make('Good', DateTime.utc(2026)));
    await File('${tmp.path}/profile_drafts/junk.json')
        .writeAsString('{not json');
    await File('${tmp.path}/profile_drafts/nodraft.json')
        .writeAsString('{"id":"x"}');
    expect((await store.loadAll()).map((d) => d.name), ['Good']);
  });
}
