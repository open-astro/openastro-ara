import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:openastroara/state/settings/loaded_section.dart';

import 'fake_section_rig.dart';

/// Load [provider]'s section from a rig that serves [data] (default: the
/// section's [rigDefaults] copy, else an empty object, which reads as the
/// section's defaults), so a test edits a value that came from the rig,
/// as ADR 0015 requires before an edit counts.
Future<void> loadSection<N extends SectionNotifier<T>, T>(
  ProviderContainer container,
  NotifierProvider<N, LoadedSection<T>> provider,
  String section, [
  Map<String, Object?>? data,
]) async {
  final ok = await container
      .read(provider.notifier)
      .load(
        FakeSectionRig({section: data ?? rigDefaults[section] ?? {}}).api(),
      );
  if (!ok) throw StateError('test rig did not serve $section');
}
