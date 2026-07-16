import 'package:flutter/material.dart';

import 'screens/screen_capture_setup.dart';
import 'screens/screen_data_and_review.dart';
import 'screens/screen_device_setup.dart';
import 'screens/screen_equipment_discovery.dart';
import 'screens/screen_profile_basics.dart';

/// Step → screen builder. All steps 1-18 (the §37.1–37.7 gear setup … sky-data
/// … review + save screens) are real forms bound to [ProfileDraft].
///
/// To wire a remaining screen: add a real ConsumerStatefulWidget under
/// `screens/` that reads `ref.read(wizardControllerProvider).draft` and
/// mutates the matching section, then register it here.

typedef WizardScreenBuilder = Widget Function(BuildContext);

final Map<int, WizardScreenBuilder> wizardScreenBuilders =
    <int, WizardScreenBuilder>{
  1: (_) => const ScreenProfileBasics(),
  2: (_) => const ScreenAlpacaConnect(),
  3: (_) => const ScreenEquipmentAssign(),
  4: (_) => const ScreenTelescope(),
  5: (_) => const ScreenCamera(),
  6: (_) => const ScreenFilterWheel(),
  7: (_) => const ScreenFocuser(),
  8: (_) => const ScreenMount(),
  9: (_) => const ScreenRotator(),
  10: (_) => const ScreenGuider(),
  11: (_) => const ScreenPlateSolve(),
  12: (_) => const ScreenAutofocus(),
  13: (_) => const ScreenFileSaving(),
  14: (_) => const ScreenImagingDefaults(),
  15: (_) => const ScreenSafety(),
  16: (_) => const ScreenSiteAltitude(),
  17: (_) => const ScreenReview(),
};
