import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../state/wizard_state.dart';
import '../../theme/ara_colors.dart';
import 'screens/screen_capture_setup.dart';
import 'screens/screen_device_setup.dart';
import 'screens/screen_equipment_discovery.dart';
import 'screens/screen_profile_basics.dart';

/// Step → screen builder. Steps 1-11 (the §37.1–37.4 "gear setup" + plate-solve
/// screens) are real forms bound to [ProfileDraft]; steps 12-18 (autofocus, file
/// saving, imaging, safety, sky data, review) are still placeholders pending
/// follow-up work.
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
  // Steps 12-18 remain placeholders until their forms land.
  for (int step = 12; step <= ProfileWizard.totalSteps; step++)
    step: (ctx) => _PlaceholderScreen(step: step),
};

class _PlaceholderScreen extends ConsumerWidget {
  final int step;
  const _PlaceholderScreen({required this.step});

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final info = ProfileWizard.steps[step]!;
    final draft = ref.watch(wizardControllerProvider).draft;
    final wasSkipped = draft.skippedScreens.contains(step);

    return Center(
      child: ConstrainedBox(
        constraints: const BoxConstraints(maxWidth: 560),
        child: Padding(
          padding: const EdgeInsets.all(24),
          child: Column(
            mainAxisSize: MainAxisSize.min,
            crossAxisAlignment: CrossAxisAlignment.start,
            children: [
              Text(
                'Stage ${info.stage} — ${info.stageLabel}',
                style: Theme.of(context).textTheme.labelLarge?.copyWith(
                      color: AraColors.textSecondary,
                      letterSpacing: 0.6,
                    ),
              ),
              const SizedBox(height: 4),
              Text(info.title,
                  style: Theme.of(context).textTheme.headlineSmall),
              const SizedBox(height: 16),
              Text(
                _placeholderBody(step),
                style: Theme.of(context).textTheme.bodyMedium?.copyWith(
                      color: AraColors.textSecondary,
                    ),
              ),
              if (wasSkipped) ...[
                const SizedBox(height: 16),
                Container(
                  padding: const EdgeInsets.symmetric(horizontal: 12, vertical: 8),
                  decoration: BoxDecoration(
                    color: AraColors.accentBusy.withValues(alpha: 0.15),
                    borderRadius: BorderRadius.circular(4),
                    border: Border.all(color: AraColors.accentBusy),
                  ),
                  child: Text(
                    'This screen was skipped — defaults will fill in until you review it in Settings.',
                    style: Theme.of(context).textTheme.bodySmall?.copyWith(
                          color: AraColors.accentBusy,
                        ),
                  ),
                ),
              ],
            ],
          ),
        ),
      ),
    );
  }

  String _placeholderBody(int step) {
    // Brief description per step, so navigating the wizard isn't a wall of
    // identical placeholders. Real form widgets replace these one-by-one in
    // Phase 12b follow-ups.
    return switch (step) {
      1 => 'Profile name (required) + site lat/lon/altitude (optional, with "Use device GPS") + '
            'site name + timezone. (§37.1)',
      2 => 'AlpacaBridge address (auto-discover via UDP 32227 or manual) + Test Connection. '
            'Alpaca-only per §52. (§37.2)',
      3 => 'Server enumerates Alpaca devices, user assigns each device slot (Camera / Filter '
            'Wheel / Focuser / Mount / Rotator / Dome / Conditions / Switch / Safety Monitor / '
            'Flat Panel / Guider) or leaves "— None". (§37.2)',
      4 => 'Telescope name, focal length (mm, required), aperture (mm, required), focal ratio '
            '(derived). Aladin survey recommendation per §36.10. (§37.3)',
      5 => 'Camera cooling target + ramp rate + warmup mode (per §28.13) + default gain/offset/bin + '
            'pixel size. Image scale auto-computed. (§37.3)',
      6 => 'Per-slot filter name/type/wavelength. Focus offsets populate automatically on first '
            'autofocus per §28.5. (§37.3)',
      7 => 'Focuser step size + backlash compensation + temperature compensation toggle/slope + '
            'max travel. (§37.3)',
      8 => 'Mount name + slew rate + park position + meridian flip behavior + settle time '
            '(pulled from Alpaca SlewSettleTime). (§37.3)',
      9 => 'Rotator min/max angle + step size + reverse direction toggle. (§37.3)',
      10 => 'PHD2 host:port + dither pixels + settle threshold + calibration cadence. (§37.3)',
      12 => 'Autofocus exposure + step size + max retries + auto-discover filter offsets toggle. '
            '(§37.4)',
      13 => 'Save directory (USB recommended per §29) + format (FITS/XISF) + compression + '
            'filename template (default per §37.4). (§37.4)',
      14 => 'Default exposure + gain/offset + frame type. Cooling target inherited from Screen 5. '
            '(§37.4)',
      15 => 'Compact safety policies: clouds/wind/rain → Pause / Abort+Park / Ignore + WILMA-'
            'offline auto-abort + alarm sound/vibrate. Full editor in Settings → Safety. (§37.5)',
      16 => 'Hard min altitude + soft warning altitude + twilight margins + max sequence runtime. '
            '(§37.5)',
      17 => 'Recommended Sky Data downloads (default-checked: Famous Targets + Star Catalogs '
            'preset, plus rig-specific surveys per §36.10). Open full Data Manager for granular '
            'control. Internet-aware — queues if offline. (§37.6)',
      18 => 'Single-page summary of every setting, with "Make Changes — jump to any screen" + '
            'Save Profile. Saving navigates to the main app shell. (§37.7)',
      _ => 'Placeholder for step $step.',
    };
  }
}
