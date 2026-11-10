import 'package:flutter/widgets.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import 'simple_plan_panel.dart';
import 'simple_screen.dart';

/// Queue: the plan's targets, the run shutter, the plan on the chip.
class SimpleQueueScreen extends SimpleScreen {
  const SimpleQueueScreen();

  @override
  Widget panel(SimpleScreenHost host) => SimpleRunPanel(
    onAdd: host.onAddTarget,
    onEdit: host.onEditTarget,
  );

  @override
  Widget get shutter => const SimpleRunShutter();

  @override
  String caption(BuildContext context, WidgetRef ref) => simpleRunChip(
    ref.watch(simpleRunProvider),
    ref.watch(simpleRunPlanProvider),
  );
}
