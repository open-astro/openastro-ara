import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/models/camera_status.dart';
import 'package:openastroara/models/guider_status.dart';
import 'package:openastroara/screens/simple/simple_preflight.dart';
import 'package:openastroara/screens/simple/simple_preview_panel.dart';
import 'package:openastroara/services/equipment_driver_api.dart';
import 'package:openastroara/state/equipment/camera_state.dart';
import 'package:openastroara/state/guider/guider_state.dart';

import 'selection_tint_contrast.dart';

class _Camera extends CameraStatusNotifier {
  @override
  Future<CameraStatus?> build() async => CameraStatus.fromJson({
    'device_id': 'c',
    'name': 'ZWO ASI533MC Pro',
    'state': 'connected',
    'capabilities': <String, dynamic>{},
  });
}

class _Guider extends GuiderStatusNotifier {
  @override
  Future<GuiderStatus?> build() async => GuiderStatus.fromJson({
    'device_id': 'g',
    'name': 'PHD2',
    'state': 'connected',
    'runtime': {'state': 'stopped'},
  });
}

void main() {
  testWidgets(
    'the selected camera row\'s detail caption reads 4.5:1 on the selection tint',
    (tester) async {
      await tester.pumpWidget(
        ProviderScope(
          overrides: [
            cameraStatusProvider.overrideWith(_Camera.new),
            guiderStatusProvider.overrideWith(_Guider.new),
            deviceDriverProvider.overrideWith(
              (ref, type) async =>
                  const DeviceDriver(name: 'ZWO ASI120MM Mini'),
            ),
          ],
          child: const MaterialApp(
            home: Scaffold(
              body: SingleChildScrollView(child: SimpleSettingsPopover()),
            ),
          ),
        ),
      );
      await tester.pump();
      await tester.pump();
      // The main camera is the selected row; its caption is the model name.
      expectReadsOnSelectionTint(
        tester,
        find.descendant(
          of: find.byKey(const Key('simple-preview-camera-main')),
          matching: find.text('ZWO ASI533MC Pro'),
        ),
      );
    },
  );

  testWidgets(
    'the current Preflight step\'s number reads 4.5:1 on the selection tint',
    (tester) async {
      await tester.pumpWidget(
        ProviderScope(
          overrides: [simplePreflightProvider.overrideWith(_AtFocus.new)],
          child: const MaterialApp(
            home: Scaffold(body: SimplePreflightStrip()),
          ),
        ),
      );
      await tester.pump();
      // Focus is the second step: its circle holds "2".
      expectReadsOnSelectionTint(tester, find.text('2'));
    },
  );
}

/// Preflight stopped on its second step.
class _AtFocus extends SimplePreflightNotifier {
  @override
  SimplePreflightStep? build() => SimplePreflightStep.focus;
}
