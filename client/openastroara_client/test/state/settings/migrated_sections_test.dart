import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/state/profile_live_sync.dart';
import 'package:openastroara/state/profile_management_state.dart';
import 'package:openastroara/state/settings/diagnostics_mode_state.dart';
import 'package:openastroara/state/settings/filenames_settings_state.dart';
import 'package:openastroara/state/settings/loaded_section.dart';
import 'package:openastroara/state/settings/notifications_settings_state.dart';
import 'package:openastroara/state/settings/migrated_sections.dart';

import '../../support/fake_section_rig.dart';

// ADR 0015 for the sections that load before they save: one list drives the
// reset and the live-sync table.

/// Every migrated section by the path the rig serves it under.
const _paths = ['diagnostics-mode', 'filenames', 'notifications', 'storage'];

FakeSectionRig _rig() => FakeSectionRig({
  for (final p in _paths) p: <String, Object?>{},
  'storage': {
    'save_directory': '/data',
    'file_format': 'fits',
    'compression': 'off',
    'filename_template': '{target}',
    'min_free_disk_warn_gb': 10,
    'min_free_disk_critical_gb': 5,
    'backup_retention_count': 3,
    'fault_log_retention_days': 30,
  },
});

ProviderContainer _container(FakeSectionRig rig) {
  final c = ProviderContainer(
    overrides: [profileApiProvider.overrideWithValue(rig.api())],
  );
  addTearDown(c.dispose);
  for (final s in migratedSections) {
    c.listen(s.provider, (_, _) {});
  }
  return c;
}

void main() {
  test('Migrated sections: every live-sync route loads exactly its own '
      'sections', () async {
    expect({for (final s in migratedSections) s.route}, _paths.toSet());
    final c = _container(_rig());
    for (final route in _paths) {
      await applyProfileUpdated(c, route);
      for (final s in migratedSections) {
        expect(
          c.read(s.provider).canSave,
          s.route == route,
          reason: '${s.route} after profile.updated for $route',
        );
      }
      resetMigratedSections(c.read);
    }
  });

  test('Migrated sections: the reset sends every one back to not loaded, '
      'saved or not', () async {
    final c = _container(_rig());
    for (final route in _paths) {
      await applyProfileUpdated(c, route);
    }
    expect(migratedSections.every((s) => c.read(s.provider).canSave), isTrue);
    resetMigratedSections(c.read);
    for (final s in migratedSections) {
      expect(c.read(s.provider).canSave, isFalse, reason: s.route);
      expect(c.read(s.provider).value, isNull, reason: s.route);
    }
  });

  // One edit per section, made on a copy the rig served.
  final edits = <String, void Function(ProviderContainer)>{
    'notifications': (c) =>
        c.read(notificationsSettingsProvider.notifier).setPushoverToken('mine'),
    'filenames': (c) => c
        .read(filenamesSettingsProvider.notifier)
        .setCompressDarksAndBias(false),
    'diagnostics-mode': (c) => c
        .read(diagnosticsModeProvider.notifier)
        .edit((_) => DiagnosticsMode.values.last),
  };

  for (final MapEntry(key: route, value: edit) in edits.entries) {
    final section = migratedSections.firstWhere((s) => s.route == route);

    test(
      '$route: Save after a failed load writes nothing to the rig',
      () async {
        final rig = _rig()..failGet.add(route);
        final c = _container(rig);
        await expectLater(
          c.read(section.provider.notifier).save(rig.api()),
          throwsA(isA<SectionNotLoadedException>()),
        );
        expect(rig.puts, isEmpty);
      },
    );

    test(
      '$route: Save over a copy another device replaced is refused',
      () async {
        final rig = _rig();
        final c = _container(rig);
        await applyProfileUpdated(c, route);
        edit(c);
        rig.savedElsewhere(route);
        await expectLater(
          c.read(section.provider.notifier).save(rig.api()),
          throwsA(isA<SectionConflictException>()),
        );
        expect(rig.puts, isEmpty);
        expect(c.read(section.provider).conflict, isTrue);
      },
    );
  }

  // The diagnostics radios save on each tap (replaceAndSave).
  const mode = 'diagnostics-mode', tapped = DiagnosticsMode.abortOnCritical;

  test('diagnostics-mode: a failed radio tap writes nothing and shows the '
      'loaded mode again', () async {
    final rig = _rig();
    final c = _container(rig);
    final n = c.read(diagnosticsModeProvider.notifier);
    await expectLater(
      n.replaceAndSave(tapped),
      throwsA(isA<SectionNotLoadedException>()),
    );
    await n.load(rig.api());
    rig.failPut.add(mode);
    await expectLater(n.replaceAndSave(tapped), throwsException);
    expect(c.read(diagnosticsModeProvider).value, DiagnosticsMode.notifyOnly);
    expect(rig.puts, isEmpty);
  });

  test('diagnostics-mode: a radio tap refused as stale keeps the choice and '
      'flags the conflict', () async {
    final rig = _rig();
    final c = _container(rig);
    await c.read(diagnosticsModeProvider.notifier).load(rig.api());
    rig.savedElsewhere(mode);
    await expectLater(
      c.read(diagnosticsModeProvider.notifier).replaceAndSave(tapped),
      throwsA(isA<SectionConflictException>()),
    );
    expect(c.read(diagnosticsModeProvider).value, tapped);
    expect(c.read(diagnosticsModeProvider).conflict, isTrue);
  });

  test('diagnostics-mode: a quick second tap is saved when the first one '
      'fails', () async {
    final rig = _rig()..failPut.add(mode);
    final c = _container(rig);
    final n = c.read(diagnosticsModeProvider.notifier);
    await n.load(rig.api());
    final first = n.replaceAndSave(DiagnosticsMode.pauseOnCritical);
    final second = n.replaceAndSave(tapped);
    await expectLater(first, throwsException);
    await second;
    expect(c.read(diagnosticsModeProvider).value, tapped);
    expect(rig.puts.single.body, {'mode': 'abort_on_critical'});
  });

  test('diagnostics-mode: two quick taps that both fail show the mode the '
      'rig holds', () async {
    final rig = _rig();
    final c = _container(rig);
    final n = c.read(diagnosticsModeProvider.notifier);
    await n.load(rig.api());
    await n.replaceAndSave(tapped);
    rig.failPut.addAll([mode, mode]);
    final first = n.replaceAndSave(DiagnosticsMode.pauseOnCritical);
    final second = n.replaceAndSave(DiagnosticsMode.notifyOnly);
    await expectLater(first, throwsException);
    await expectLater(second, throwsException);
    expect(c.read(diagnosticsModeProvider).value, tapped);
    expect(rig.puts.single.body, {'mode': 'abort_on_critical'});
  });
}
