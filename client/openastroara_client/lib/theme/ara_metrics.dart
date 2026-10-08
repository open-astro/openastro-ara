import 'package:flutter/material.dart';

import 'ara_colors.dart';

/// §25.2 layout + type tokens (Run-redesign S1). One spacing scale and one
/// small type ramp so redesigned surfaces stop inventing per-widget 11/12/13px
/// styles. Adopted incrementally — only code a redesign slice touches converts;
/// this is NOT a repo-wide restyle.
class AraSpace {
  static const double s4 = 4;
  static const double s8 = 8;
  static const double s12 = 12;
  static const double s16 = 16;
  static const double s24 = 24;
}

class AraText {
  /// Pane / dialog titles.
  static const TextStyle title = TextStyle(
      fontSize: 15, fontWeight: FontWeight.w600, color: AraColors.textPrimary);

  /// Uppercased group headers (palette categories, inspector sections).
  static const TextStyle section = TextStyle(
      fontSize: 11,
      fontWeight: FontWeight.w600,
      letterSpacing: 0.6,
      color: AraColors.textSecondary);

  /// Default row/body text.
  static const TextStyle body =
      TextStyle(fontSize: 13, color: AraColors.textPrimary);

  /// Secondary line under a body row.
  static const TextStyle caption =
      TextStyle(fontSize: 11.5, color: AraColors.textSecondary);

  /// Numeric readouts (timers, counts) — tabular figures so they don't jitter.
  static const TextStyle numeric = TextStyle(
      fontSize: 13,
      color: AraColors.textPrimary,
      fontFeatures: [FontFeature.tabularFigures()]);
}

/// Window-size tiers, in logical px (what Flutter sees after OS scaling).
class AraBreakpoints {
  /// Under this window height a pane goes compact: tighter padding, secondary
  /// lines dropped. The common 1080p laptop at 150 % scaling leaves ≈ 640 px of
  /// window; a 1470×956 MacBook Air ≈ 860.
  static const double shortHeight = 820;

  static bool isShort(BuildContext context) =>
      MediaQuery.sizeOf(context).height < shortHeight;

  /// Instruments sit side by side (picture | readout) from this pane width,
  /// and stack below it (tablet portrait, phone).
  static const double sideBySideWidth = 760;

  /// A short window keeps them side by side down to this width (a 1100-wide
  /// laptop window), since stacking them would scroll.
  static const double sideBySideWidthShort = 600;

  static double sideBySide(BuildContext context) =>
      isShort(context) ? sideBySideWidthShort : sideBySideWidth;
}
