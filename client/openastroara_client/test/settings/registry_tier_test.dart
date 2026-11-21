import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/settings/registry.dart';

void main() {
  test(
    'every entry has a tier; Simple is the Advanced toggle and the three focus basics (#1544)',
    () {
      expect(settingsRegistry, isNotEmpty);
      expect(
        {for (final s in settingsRegistry) s.tier},
        {SettingTier.simple, SettingTier.advanced},
      );
      expect(
        {
          for (final s in settingsRegistry)
            if (s.tier == SettingTier.simple) s.id,
        },
        {
          'app.settings.advanced',
          'img.autofocus.during_plans',
          'img.autofocus.keep_focus_sharp',
          'img.autofocus.af_filter',
        },
      );
    },
  );

  test(
    'derived-by is set for the focus values something else computes (#1544)',
    () {
      final derived = {
        for (final s in settingsRegistry)
          if (s.derivedBy != null) s.id: s.derivedBy!,
      };
      expect(derived.keys.toSet(), {
        'img.autofocus.run_after_filter_change',
        'img.autofocus.trigger_temp_delta_c',
        'img.autofocus.trigger_hfr_drift_pct',
        'img.autofocus.every_n_hours',
        'img.autofocus.step_size',
      });
      for (final text in derived.values) {
        expect(text.trim(), isNotEmpty);
      }
    },
  );
}
