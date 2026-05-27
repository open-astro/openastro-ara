import 'package:flutter/material.dart';

/// Color tokens for the ARA Flutter client per playbook §25.2.
///
/// Material's default dark theme is too light/colorful for an observatory
/// app used in the dark — these tokens give us the muted dark surface +
/// saturated status accents that match NINA's information-dense layout.
class AraColors {
  // Backgrounds
  static const bgPrimary = Color(0xFF1A1A1A);
  static const bgPanel = Color(0xFF262626);
  static const bgPanelAlt = Color(0xFF2E2E2E);
  static const bgInput = Color(0xFF333333);
  static const border = Color(0xFF404040);

  // Text
  static const textPrimary = Color(0xFFE0E0E0);
  static const textSecondary = Color(0xFFA0A0A0);
  static const textDisabled = Color(0xFF606060);

  // Status accents
  static const accentConnected = Color(0xFF4CAF50);
  static const accentBusy = Color(0xFFFFB300);
  static const accentError = Color(0xFFE53935);
  static const accentInfo = Color(0xFF42A5F5);
  static const accentDisconnected = Color(0xFF606060);

  // Selection / buttons
  static const selectionBg = Color(0xFF1976D2);
  static const selectionFg = Color(0xFFFFFFFF);
  static const buttonPrimary = Color(0xFF1565C0);
  static const buttonSecondary = Color(0xFF424242);
}
