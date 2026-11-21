import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/state/profile_management_state.dart';
import 'package:openastroara/state/settings/diagnostics_mode_state.dart';
import 'package:openastroara/state/settings/filenames_settings_state.dart';
import 'package:openastroara/state/settings/loaded_section.dart';
import 'package:openastroara/state/settings/notifications_settings_state.dart';

import '../../support/fake_section_rig.dart';

/// ADR 0015 rule 3 — a key the rig leaves out is missing, never the client's
/// old default: the load fails, the section stays not loaded, and Save sends
/// nothing. Each section here read a missing key as its default before.
void main() {
  /// A rig whose [section] reply lacks [key], the section's notifier
  /// loaded from it, and a Save that must not reach the rig.
  Future<(FakeSectionRig, LoadedSection<Object?>, Object?)> loadThenSave(
    String section,
    NotifierProvider<SectionNotifier<Object?>, LoadedSection<Object?>> provider,
    String key,
  ) async {
    final rig = FakeSectionRig({
      section: {...rigDefaults[section]!}..remove(key),
    });
    final c = ProviderContainer(
      overrides: [profileApiProvider.overrideWithValue(rig.api())],
    );
    addTearDown(c.dispose);
    final n = c.read(provider.notifier);
    expect(await n.load(rig.api()), isFalse, reason: 'the load fails');
    Object? refused;
    try {
      await n.save(rig.api());
    } catch (e) {
      refused = e;
    }
    return (rig, c.read(provider), refused);
  }

  test(
    'filenames: a rig reply missing a key fails the load and Save sends no PUT',
    () async {
      final (rig, s, refused) = await loadThenSave(
        'filenames',
        filenamesSettingsProvider,
        'header_site',
      );
      expect(s.value, isNull, reason: 'never the client default (on)');
      expect(s.lastFailure, isNotNull);
      expect(refused, isA<SectionNotLoadedException>());
      expect(rig.puts, isEmpty);
    },
  );

  test('notifications: a rig reply missing a key fails the load and Save sends '
      'no PUT', () async {
    final (rig, s, refused) = await loadThenSave(
      'notifications',
      notificationsSettingsProvider,
      'sound_alert',
    );
    expect(s.value, isNull, reason: 'never the client default (on)');
    expect(refused, isA<SectionNotLoadedException>());
    expect(rig.puts, isEmpty);
  });

  test(
    'diagnostics mode: a rig reply with no mode fails the load and Save sends '
    'no PUT',
    () async {
      final (rig, s, refused) = await loadThenSave(
        'diagnostics-mode',
        diagnosticsModeProvider,
        'mode',
      );
      expect(s.value, isNull, reason: 'never the client default (notify only)');
      expect(refused, isA<SectionNotLoadedException>());
      expect(rig.puts, isEmpty);
    },
  );
}
