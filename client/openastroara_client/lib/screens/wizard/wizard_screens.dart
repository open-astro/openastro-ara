import 'package:flutter/material.dart';

import 'screens/screen_data_and_review.dart';
import 'screens/screen_equipment_discovery.dart';
import 'screens/screen_guiding.dart';
import 'screens/screen_profile_basics.dart';
import 'screens/screen_your_equipment.dart';

/// §76.2 — step → screen builder for the five Wizard 2.0 screens. All are
/// real forms bound to [ProfileDraft]; the "Your equipment" screen reads
/// device facts from Alpaca instead of asking for them.

typedef WizardScreenBuilder = Widget Function(BuildContext);

final Map<int, WizardScreenBuilder> wizardScreenBuilders =
    <int, WizardScreenBuilder>{
  1: (_) => const ScreenProfileBasics(),
  2: (_) => const ScreenAlpacaConnect(),
  3: (_) => const ScreenYourEquipment(),
  4: (_) => const ScreenGuider(),
  5: (_) => const ScreenReview(),
};
