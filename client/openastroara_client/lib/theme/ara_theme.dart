import 'package:flutter/material.dart';

import 'ara_colors.dart';

/// Material 3 dark theme wired to the §25.2 color tokens.
ThemeData buildAraTheme() {
  const colorScheme = ColorScheme.dark(
    surface: AraColors.bgPrimary,
    surfaceContainerHigh: AraColors.bgPanel,
    surfaceContainerHighest: AraColors.bgPanelAlt,
    primary: AraColors.buttonPrimary,
    onPrimary: AraColors.selectionFg,
    secondary: AraColors.accentInfo,
    error: AraColors.accentError,
    outline: AraColors.border,
    onSurface: AraColors.textPrimary,
    onSurfaceVariant: AraColors.textSecondary,
  );

  return ThemeData(
    useMaterial3: true,
    brightness: Brightness.dark,
    colorScheme: colorScheme,
    scaffoldBackgroundColor: AraColors.bgPrimary,
    dividerColor: AraColors.border,
    appBarTheme: const AppBarTheme(
      backgroundColor: AraColors.bgPanel,
      foregroundColor: AraColors.textPrimary,
      elevation: 0,
    ),
    navigationRailTheme: const NavigationRailThemeData(
      backgroundColor: AraColors.bgPanel,
      indicatorColor: AraColors.selectionBg,
      selectedIconTheme: IconThemeData(color: AraColors.selectionFg),
      unselectedIconTheme: IconThemeData(color: AraColors.textSecondary),
      selectedLabelTextStyle: TextStyle(color: AraColors.textPrimary),
      unselectedLabelTextStyle: TextStyle(color: AraColors.textSecondary),
    ),
    cardTheme: CardThemeData(
      color: AraColors.bgPanel,
      shape: RoundedRectangleBorder(
        borderRadius: BorderRadius.circular(6),
        side: const BorderSide(color: AraColors.border),
      ),
    ),
    inputDecorationTheme: const InputDecorationTheme(
      filled: true,
      fillColor: AraColors.bgInput,
      border: OutlineInputBorder(),
    ),
    filledButtonTheme: FilledButtonThemeData(
      style: FilledButton.styleFrom(
        backgroundColor: AraColors.buttonPrimary,
        foregroundColor: AraColors.selectionFg,
      ),
    ),
  );
}
