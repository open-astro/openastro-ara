import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../l10n/l10n.dart';
import '../../services/profile_api.dart';
import '../../state/profile_management_state.dart';
import '../../state/settings/loaded_section.dart';
import '../../util/friendly_error.dart';
import 'section_load_state.dart';

/// A section provider as the panel mixin sees it (the notifiers differ only in
/// their value type, which the mixin never reads).
typedef PanelSection =
    NotifierProvider<SectionNotifier<Object?>, LoadedSection<Object?>>;

/// ADR 0015 — what every settings panel does with the sections it edits: load
/// them when it opens (and again when one goes back to not loaded, as after a
/// rig or profile change), show [SectionNotLoaded] in place of fields that hold
/// no value, show [SectionConflictBanner] after a 412, and save with one
/// refusal message. A panel with sections of its own name them in [sections];
/// a section it still saves the old way stays out of the list.
mixin SectionPanel<W extends ConsumerStatefulWidget> on ConsumerState<W> {
  /// The sections this panel edits.
  List<PanelSection> get sections;

  @override
  void initState() {
    super.initState();
    WidgetsBinding.instance.addPostFrameCallback((_) => loadSections());
  }

  /// Load every section again from the rig (also the Retry and Reload action).
  /// A failure is kept in the section and shown by [sectionGate].
  Future<void> loadSections() async {
    final api = ref.read(profileApiProvider);
    if (api == null || !mounted) return;
    await Future.wait([
      for (final s in sections) ref.read(s.notifier).load(api),
    ]);
  }

  /// Load again when a section goes back to not loaded with no failure (a new
  /// rig or profile, migrated_sections.dart). Call from `build`, or through
  /// [sectionGate].
  void reloadWhenReset() {
    for (final s in sections) {
      ref.listen(s, (_, next) {
        if (next.source == SectionSource.notLoaded &&
            next.lastFailure == null) {
          loadSections();
        }
      });
    }
  }

  /// Call first in `build`. Returns what to show instead of the fields while a
  /// section holds no value, or null when every section is loaded.
  Widget? sectionGate() {
    reloadWhenReset();
    for (final s in sections) {
      final v = ref.watch(s);
      if (v.value == null) {
        return SectionNotLoaded(section: v, onRetry: loadSections);
      }
    }
    return null;
  }

  /// The banner for a section the rig refused as changed, or null.
  Widget? sectionConflict() => sections.any((s) => ref.watch(s).conflict)
      ? SectionConflictBanner(onReload: loadSections)
      : null;

  /// Save [only] (default: all of this panel's sections), in order. Shows
  /// "Saved." or the refusal in a snackbar and returns the refusal text, or
  /// null when everything was saved.
  ///
  /// [andThen] saves what the panel still saves the old way, after the
  /// sections and before the message.
  Future<String?> saveSections({
    List<PanelSection>? only,
    Future<void> Function(ProfileApi api)? andThen,
  }) async {
    final messenger = ScaffoldMessenger.of(context);
    final l10n = context.l10n;
    final api = ref.read(profileApiProvider);
    String? error;
    if (api == null) {
      error = l10n.deviceChooserNotConnected;
    } else {
      try {
        for (final s in only ?? sections) {
          await ref.read(s.notifier).save(api);
        }
        await andThen?.call(api);
      } catch (e) {
        error = e is SectionNotLoadedException || e is SectionConflictException
            ? e.toString()
            : friendlyError(l10n, e, action: l10n.actionSave);
      }
    }
    if (mounted) {
      messenger.showSnackBar(SnackBar(content: Text(error ?? 'Saved.')));
    }
    return error;
  }
}
