import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/screens/simple/simple_focus_panel.dart';
import 'package:openastroara/screens/simple/simple_guide_panel.dart';
import 'package:openastroara/screens/simple/simple_plan_panel.dart';
import 'package:openastroara/screens/simple/simple_polar_panel.dart';
import 'package:openastroara/screens/simple/simple_preview_panel.dart';
import 'package:openastroara/screens/simple/simple_queue_screen.dart';
import 'package:openastroara/screens/simple/simple_screen.dart';
import 'package:openastroara/state/simple_messages_state.dart';
import 'package:openastroara/state/simple_preflight_state.dart';

void main() {
  test('each Screen supplies its own shutter', () {
    expect(SimpleScreen.of(SimpleModeScreen.preview).shutter, isA<SimpleShutter>());
    expect(SimpleScreen.of(SimpleModeScreen.focus).shutter, isA<SimpleFocusShutter>());
    expect(SimpleScreen.of(SimpleModeScreen.polarAlign).shutter, isA<SimplePolarShutter>());
    expect(SimpleScreen.of(SimpleModeScreen.guide).shutter, isA<SimpleGuideShutter>());
    expect(SimpleScreen.of(SimpleModeScreen.plan).shutter, isA<SimpleRunShutter>());
  });

  test('Preflight is not a Screen: before its first step Preview shows', () {
    expect(SimpleScreen.of(SimpleModeScreen.preflight).shutter, isA<SimpleShutter>());
  });

  test("Queue's panel calls back into the shell to add and edit targets", () {
    var added = 0;
    int? edited;
    final panel = SimpleScreen.of(SimpleModeScreen.plan).panel(SimpleScreenHost(
      onAddTarget: () => added++,
      onEditTarget: (i) => edited = i,
    )) as SimpleRunPanel;
    panel.onAdd();
    panel.onEdit(2);
    expect(added, 1);
    expect(edited, 2);
  });

  test('a status pill opens the Screen that posted it; anything else opens Preview', () {
    expect(SimpleScreen.showing(SimpleStatusOrigin.plan), SimpleModeScreen.plan);
    expect(SimpleScreen.showing(SimpleStatusOrigin.guide), SimpleModeScreen.guide);
    expect(SimpleScreen.showing(SimpleStatusOrigin.focus), SimpleModeScreen.focus);
    expect(SimpleScreen.showing(SimpleStatusOrigin.polar), SimpleModeScreen.polarAlign);
    expect(SimpleScreen.showing(SimpleStatusOrigin.camera), SimpleModeScreen.preview);
  });

  test("Queue's sheet opens to add, and Image this over a run adds to the queue", () {
    final c = ProviderContainer();
    addTearDown(c.dispose);
    final sheet = c.read(simpleQueueSheetProvider.notifier);
    sheet.open();
    expect(c.read(simpleQueueSheetProvider), (adding: true, editing: null));
    sheet.close();
    expect(c.read(simpleQueueSheetProvider).adding, isFalse);
    sheet.pick(overRun: true);
    expect(c.read(simpleQueueSheetProvider).adding, isTrue);
    sheet.pick(overRun: false);
    expect(c.read(simpleQueueSheetProvider).adding, isFalse);
  });
}
