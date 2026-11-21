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
/// rig or profile change), show [SectionNotLoaded] in place of fields with no
/// copy from the rig, [SectionConflictBanner] after a 412, and save with one
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

  /// Load every section again from the rig, dropping their unsaved edits (the
  /// Retry and Reload action). [sectionGate] shows a failure kept in a section.
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
  /// section holds no copy from the rig (an offline cache copy is not
  /// editable), or null when every section is loaded.
  Widget? sectionGate() {
    reloadWhenReset();
    for (final s in sections) {
      final v = ref.watch(s);
      if (!v.canSave) {
        return SectionNotLoaded(section: v, onRetry: loadSections);
      }
    }
    return null;
  }

  /// The banner for a section the rig refused as changed, or null.
  Widget? sectionConflict() => sections.any((s) => ref.watch(s).conflict)
      ? SectionConflictBanner(onReload: loadSections)
      : null;

  /// Save this panel's sections, in order. Shows "Saved." or the refusal in a
  /// snackbar and returns the refusal text, or null when everything was saved.
  ///
  /// [andThen] saves what the panel still saves the old way, after the
  /// sections and before the message; when it fails after the sections were
  /// saved, the message starts with [andThenFailed]. When a section fails
  /// after an earlier one was saved, the message starts with [partlySaved].
  Future<String?> saveSections({
    Future<void> Function(ProfileApi api)? andThen,
    String andThenFailed = '',
    String partlySaved = '',
  }) async {
    final messenger = ScaffoldMessenger.of(context);
    final l10n = context.l10n;
    final api = ref.read(profileApiProvider);
    String? error;
    var saved = false;
    var sectionsSaved = 0;
    if (api == null) {
      error = l10n.deviceChooserNotConnected;
    } else {
      try {
        for (final s in sections) {
          await ref.read(s.notifier).save(api);
          sectionsSaved++;
        }
        saved = true;
        await andThen?.call(api);
      } catch (e) {
        error = e is SectionNotLoadedException || e is SectionConflictException
            ? e.toString()
            : friendlyError(l10n, e, action: l10n.actionSave);
        if (saved) {
          error = '$andThenFailed$error';
        } else if (sectionsSaved > 0) {
          error = '$partlySaved$error';
        }
      }
    }
    if (mounted) {
      messenger.showSnackBar(SnackBar(content: Text(error ?? 'Saved.')));
    }
    return error;
  }
}
