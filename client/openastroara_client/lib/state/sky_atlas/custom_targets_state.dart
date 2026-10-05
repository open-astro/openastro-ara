import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../services/custom_targets_service.dart';

/// Override in tests with a temp-dir service.
final customTargetsServiceProvider =
    Provider<CustomTargetsService>((ref) => CustomTargetsService());

/// The recent typed-in targets, newest first (see [CustomTargetsService]).
class CustomTargetsNotifier extends AsyncNotifier<List<CustomTarget>> {
  @override
  Future<List<CustomTarget>> build() =>
      ref.watch(customTargetsServiceProvider).load();

  Future<void> remember(CustomTarget t) async {
    final list = await ref.read(customTargetsServiceProvider).remember(t);
    if (ref.mounted) state = AsyncData(list);
  }

  Future<void> forget(CustomTarget t) async {
    final list = await ref.read(customTargetsServiceProvider).forget(t);
    if (ref.mounted) state = AsyncData(list);
  }
}

final customTargetsProvider =
    AsyncNotifierProvider<CustomTargetsNotifier, List<CustomTarget>>(
        CustomTargetsNotifier.new);
