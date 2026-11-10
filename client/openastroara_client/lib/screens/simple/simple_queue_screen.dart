import 'dart:async';

import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../state/simple_preflight_state.dart';
import '../../state/simple_messages_state.dart';
import '../../l10n/l10n.dart';
import '../../state/sequencer/sequence_list_state.dart';
import 'simple_plan_panel.dart';
import 'simple_screen.dart';

/// Queue: the plan's targets, the run shutter, the plan on the chip.
class SimpleQueueScreen extends SimpleScreen {
  const SimpleQueueScreen();

  @override
  SimpleModeScreen get id => SimpleModeScreen.plan;

  @override
  SimpleStatusOrigin get origin => SimpleStatusOrigin.plan;

  @override
  Widget panel(SimpleScreenHost host) => SimpleRunPanel(
    onAdd: host.onAddTarget,
    onEdit: host.onEditTarget,
  );

  @override
  Widget overlay(SimpleScreenHost host) => SimpleRunSheet(
    editing: host.editing,
    onShowSky: host.onShowSky ?? () {},
    onPickInSky: host.onPickInSky ?? () {},
    onCreated: host.onSheetDone ?? () {},
  );

  @override
  Widget tracker(SimpleScreenHost host) =>
      SimpleRunTracker(onTap: host.onTrackerTap ?? () {});

  @override
  Widget get shutter => const SimpleRunShutter();

  @override
  String caption(BuildContext context, WidgetRef ref) => simpleRunChip(
    ref.watch(simpleRunProvider),
    ref.watch(simpleRunPlanProvider),
  );

  /// The first ⌘. / Ctrl+. asks; a second within 3 s stops (the keyboard's
  /// stand-in for the shutter's 2 s hold, as the Emergency Stop does).
  @override
  void onStopShortcut(BuildContext context, WidgetRef ref) {
    final phase = simpleRunPhase(
      ref.read(simpleRunProvider),
      ref.read(sequenceRunStateProvider).value,
    );
    if (phase != SimpleRunPhase.imaging &&
        phase != SimpleRunPhase.pausing &&
        phase != SimpleRunPhase.paused) {
      return;
    }
    final mac = const {
      TargetPlatform.macOS,
      TargetPlatform.iOS,
    }.contains(Theme.of(context).platform);
    ref
        .read(_stopAskProvider.notifier)
        .press(context.l10n.shellStopAgain(mac ? '⌘.' : 'Ctrl+.'));
  }
}

/// The ⌘. question while it waits for the second press.
final _stopAskProvider = NotifierProvider<_StopAsk, int?>(_StopAsk.new);

class _StopAsk extends Notifier<int?> {
  Timer? _disarm;

  @override
  int? build() {
    ref.onDispose(() => _disarm?.cancel());
    return null;
  }

  void press(String question) {
    final messages = ref.read(simpleMessagesProvider.notifier);
    _disarm?.cancel();
    if (state case final asked?) {
      messages.dismiss(asked);
      state = null;
      ref.read(simpleRunProvider.notifier).stop();
      return;
    }
    state = messages.show(SimpleMessageKind.question, question);
    _disarm = Timer(const Duration(seconds: 3), () {
      if (state case final asked?) messages.dismiss(asked);
      state = null;
    });
  }
}

/// Queue's sheet, opened from the queue panel: to add a target (also over a
/// run in progress), or on queued target [editing]'s rows.
typedef SimpleQueueSheet = ({bool adding, int? editing});

final simpleQueueSheetProvider =
    NotifierProvider<SimpleQueueSheetNotifier, SimpleQueueSheet>(
      SimpleQueueSheetNotifier.new,
    );

class SimpleQueueSheetNotifier extends Notifier<SimpleQueueSheet> {
  @override
  SimpleQueueSheet build() => (adding: false, editing: null);

  /// Add target, or edit row [edit]: its rows load into the sheet.
  void open({int? edit}) {
    if (edit != null) {
      ref
          .read(simpleRunPlanProvider.notifier)
          .load(ref.read(simpleRunProvider).targets[edit].steps);
    }
    state = (adding: true, editing: edit);
  }

  /// Leaving an edit drops its rows: the sheet plans the next target afresh.
  void close() {
    if (state.editing != null) ref.invalidate(simpleRunPlanProvider);
    state = (adding: false, editing: null);
  }

  /// Image this: over a run the sheet adds to its queue.
  void pick({required bool overRun}) => state = (adding: overRun, editing: null);
}
