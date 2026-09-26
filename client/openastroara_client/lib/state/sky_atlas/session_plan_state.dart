import 'package:flutter/material.dart' show TimeOfDay;
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../services/tonight_sky_api.dart';
import '../../util/session_planner.dart';

/// The "Plan my night" dialog's working state, kept OUTSIDE the dialog so a
/// plan survives the dialog closing: "Show on atlas" has to dismiss the modal
/// to let the user see the planetarium, and reopening the dialog must bring
/// the same plan back rather than a blank form. [ranked] is the list the plan
/// was drawn from — the swap menu picks alternatives out of it.
class SessionPlanState {
  final TimeOfDay start;
  final TimeOfDay end;
  final int targetCount;
  final SessionPlan? plan;
  final List<TonightSkyObject> ranked;
  final SessionOverheads overheads;

  const SessionPlanState({
    this.start = const TimeOfDay(hour: 22, minute: 0),
    this.end = const TimeOfDay(hour: 1, minute: 0),
    this.targetCount = 1,
    this.plan,
    this.ranked = const [],
    this.overheads = const SessionOverheads(),
  });

  SessionPlanState copyWith({
    TimeOfDay? start,
    TimeOfDay? end,
    int? targetCount,
    SessionPlan? plan,
    bool clearPlan = false,
    List<TonightSkyObject>? ranked,
    SessionOverheads? overheads,
  }) =>
      SessionPlanState(
        start: start ?? this.start,
        end: end ?? this.end,
        targetCount: targetCount ?? this.targetCount,
        plan: clearPlan ? null : (plan ?? this.plan),
        ranked: ranked ?? this.ranked,
        overheads: overheads ?? this.overheads,
      );
}

class SessionPlanNotifier extends Notifier<SessionPlanState> {
  @override
  SessionPlanState build() => const SessionPlanState();

  /// Window / count edits invalidate the plan — it no longer describes them.
  void setStart(TimeOfDay t) =>
      state = state.copyWith(start: t, clearPlan: true);
  void setEnd(TimeOfDay t) => state = state.copyWith(end: t, clearPlan: true);
  void setTargetCount(int n) =>
      state = state.copyWith(targetCount: n, clearPlan: true);

  void setPlan(SessionPlan plan,
      {required List<TonightSkyObject> ranked,
      required SessionOverheads overheads}) {
    state = state.copyWith(plan: plan, ranked: ranked, overheads: overheads);
  }

  /// Put [replacement] into slice [index] (see [swapPlanTarget]).
  void swap(int index, TonightSkyObject replacement) {
    final plan = state.plan;
    if (plan == null) return;
    state = state.copyWith(
      plan: swapPlanTarget(plan, index, replacement,
          overheads: state.overheads),
    );
  }
}

final sessionPlanProvider =
    NotifierProvider<SessionPlanNotifier, SessionPlanState>(
        SessionPlanNotifier.new);
