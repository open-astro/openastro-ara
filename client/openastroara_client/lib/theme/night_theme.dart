import 'package:flutter/material.dart';

import 'ara_theme.dart';

/// Red-tinted Material 3 night theme for dark-site observing. Built on the
/// normal Ara theme (so component styling is preserved) with a red colour
/// scheme in place of the neutral one — used by the `--dart-define=
/// NIGHT_MODE_KIND=theme` build.
ThemeData buildNightTheme() {
  const scheme = ColorScheme.dark(
    surface: Color(0xFF15090A),
    surfaceContainerHigh: Color(0xFF1F0D0F),
    surfaceContainerHighest: Color(0xFF2A1114),
    primary: Color(0xFFFF3B30),
    onPrimary: Color(0xFF220000),
    secondary: Color(0xFFFF6B5A),
    onSecondary: Color(0xFF2A0000),
    error: Color(0xFFFF453A),
    outline: Color(0xFF5A2A2A),
    outlineVariant: Color(0xFF5A2A2A),
    onSurface: Color(0xFFF3C9C9),
    onSurfaceVariant: Color(0xFFD49A9A),
  );
  return buildAraTheme().copyWith(
    colorScheme: scheme,
    scaffoldBackgroundColor: const Color(0xFF15090A),
    dividerColor: const Color(0xFF5A2A2A),
  );
}
