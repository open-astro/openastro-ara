import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_riverpod/misc.dart' show ProviderListenable;
import 'package:openastroara/state/settings/loaded_section.dart';

import 'fake_section_rig.dart';

/// Load [provider]'s section from a rig that serves [body] (an empty object
/// reads as the section's defaults), so a test edits a value that came from
/// the rig, as ADR 0015 requires before an edit counts.
Future<void> loadSection<N extends SectionNotifier<T>, T>(
  ProviderContainer container,
  NotifierProvider<N, LoadedSection<T>> provider,
  String section, [
  Object? body = const <String, Object?>{},
]) async {
  final ok = await container
      .read(provider.notifier)
      .load(FakeSectionRig({section: body}).api());
  if (!ok) throw StateError('test rig did not serve $section');
}

/// Wait until [provider]'s self-loading section holds the rig's copy.
Future<void> settleLoaded<T>(
  ProviderContainer container,
  ProviderListenable<LoadedSection<T>> provider,
) => settle(() => container.read(provider).canSave);
