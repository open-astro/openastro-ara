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

/// Corner radius by role (DESIGN.md Shapes). Widgets build their own
/// `BorderRadius` from these, as they do with [AraSpace].
class AraRadius {
  /// Fields and small controls.
  static const double field = 4;
  static const double card = 6;

  /// Larger containers.
  static const double container = 8;

  /// Simple Mode's panels and device windows.
  static const double simplePanel = 10;
  static const double pill = 12;
}

/// A locked control's dim (DESIGN.md, the Switches window): Material's
/// disabled opacity.
class AraOpacity {
  static const double locked = 0.38;
}

/// Backdrop blur (DESIGN.md `panel-simple`, value in .impeccable/design.json):
/// Simple Mode's panels blur the image behind their 90 % scrim, so their words
/// stand off a busy frame.
class AraBlur {
  /// `--dart-define=ARA_BLUR_OFF=true` turns the blur off, to measure its cost.
  static const double panel = bool.fromEnvironment('ARA_BLUR_OFF') ? 0 : 5;
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

/// Simple Mode body text: 14 px minimum (brief), tabular for live numbers. The
/// Sky Album shares it, since it follows Simple Mode's density.
const kSimpleText = TextStyle(
  fontSize: 14,
  color: AraColors.textPrimary,
  fontFeatures: [FontFeature.tabularFigures()],
);

/// Simple Mode's type sizes by role (DESIGN.md Typography), for a style
/// that is not one of the named ones below.
class AraFontSize {
  static const double simpleCaption = 12;
  static const double simpleUnit = 13;

  /// A direction pad cell's N / S / E / W (#1649).
  static const double simplePadLetter = 18;
  static const double simpleReading = 22;
  static const double simpleReadingLarge = 30;

  /// Monitor's top line (#1724).
  static const double monitorVerdict = 20;

  /// Monitor's frame countdown, read from across the room; the smaller size
  /// from ten minutes up, and a wait in words in place of a countdown.
  static const double monitorCountdown = 120;
  static const double monitorCountdownLong = 96;
  static const double monitorWord = 44;
}

/// Simple Mode's caption: a tile's label, a window's secondary line.
const kSimpleCaption = TextStyle(
  fontSize: AraFontSize.simpleCaption,
  color: AraColors.textSecondary,
);

/// A reading's unit beside its number ("°C", "%").
const kSimpleUnit = TextStyle(
  fontSize: AraFontSize.simpleUnit,
  color: AraColors.textSecondary,
  fontFeatures: [FontFeature.tabularFigures()],
);

/// A tile's reading (the Weather window).
const kSimpleReading = TextStyle(
  fontSize: AraFontSize.simpleReading,
  fontWeight: FontWeight.w600,
  color: AraColors.textPrimary,
  fontFeatures: [FontFeature.tabularFigures()],
);

/// A window's one big reading (the Focuser and Rotator windows).
const kSimpleReadingLarge = TextStyle(
  fontSize: AraFontSize.simpleReadingLarge,
  fontWeight: FontWeight.w600,
  color: AraColors.textPrimary,
  fontFeatures: [FontFeature.tabularFigures()],
);

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
