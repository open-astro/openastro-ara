import 'package:flutter/widgets.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../l10n/l10n.dart';
import '../../state/profile_management_state.dart' show profileApiProvider;
import '../../state/settings/loaded_section.dart';
import '../../state/simple_messages_state.dart';
import '../../util/friendly_error.dart';

/// ADR 0015 for Simple Mode: every write a Simple Mode window makes to a
/// profile section goes through here. It loads the rig's copy first, applies
/// [change] to that copy and saves it with the loaded ETag, so the edit never
/// rides on a client default and never on a copy that went stale between taps.
///
/// It refuses, writing nothing, when no rig is connected, when the load fails
/// or a reset dropped its result, and when the rig answers 412. The refusal is
/// a pill titled [title] with the reason as its detail; the section exceptions
/// are told by type, never by status code or message text (ADR 0022 rule 5).
/// A failed save (not a 412) puts the rig's copy back, and a 412 keeps the
/// edit and sets the section's `conflict` (`replaceAndSave`). Returns whether
/// the rig took the change.
Future<bool> simpleSaveSection<T>(
  BuildContext context,
  WidgetRef ref,
  NotifierProvider<SectionNotifier<T>, LoadedSection<T>> provider,
  T Function(T current) change, {
  required String title,
  SimpleStatusOrigin? origin,
}) async {
  final notify = userNotifier(context, origin: origin);
  final l10n = context.l10n;
  void refuse(String detail) => notify(title, detail: detail, error: true);
  final api = ref.read(profileApiProvider);
  if (api == null) {
    refuse(l10n.shellRigNotConnected);
    return false;
  }
  final n = ref.read(provider.notifier);
  // The window may close during the load: read the section through the
  // container, not the widget's ref.
  final container = ProviderScope.containerOf(context, listen: false);
  try {
    // A load whose result a reset dropped still returns true, with no value.
    final loaded = await n.load(api);
    final now = container.read(provider);
    if (!loaded || !now.canSave) {
      throw SectionNotLoadedException(now.lastFailure);
    }
    await n.replaceAndSave(change(now.value as T));
    return true;
  } on SectionNotLoadedException {
    refuse(l10n.simpleSectionNotLoaded);
  } on SectionConflictException {
    refuse(l10n.simpleSectionConflict);
  } catch (e) {
    refuse(failureReason(l10n, e));
  }
  return false;
}
