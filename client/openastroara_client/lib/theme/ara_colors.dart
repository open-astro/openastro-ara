import 'package:flutter/material.dart';

/// Color tokens for the ARA Flutter client per playbook §25.2.
///
/// Material's default dark theme is too light/colorful for an observatory
/// app used in the dark — these tokens give us the muted dark surface +
/// saturated status accents that match NINA's information-dense layout.
///
/// Consistency ground rules (from the retired RUN_REDESIGN design doc; they
/// apply app-wide, not just to the Run tab):
/// * One spacing scale — 8 / 12 / 16 / 24 — and the Options-shell type ramp.
/// * Accents only through semantic roles (state chips, category hues,
///   destructive actions), never as decoration.
/// * Copy in product voice: verbs, sentence case, no NINA or daemon enum
///   names leaking into the UI.
/// * Every state (idle / offline / draft / running / paused / attention /
///   failed / done) has a designed look; nothing falls back to grey text.
/// * Warnings pair icon + colour + text, never colour alone.
class AraColors {
  // Backgrounds
  static const bgPrimary = Color(0xFF1A1A1A);
  static const bgPanel = Color(0xFF262626);
  static const bgPanelAlt = Color(0xFF2E2E2E);
  static const bgInput = Color(0xFF333333);
  static const border = Color(0xFF404040);

  /// Simple Mode's floating panels: [bgPrimary] at 90 % over the frame
  /// (DESIGN.md `overlay-scrim`), so their text reads over a bright frame too.
  static const overlayScrim = Color(0xE61A1A1A);

  /// Error words on Simple Mode's panels: [accentError] lightened so it reads
  /// at 4.5:1 over the scrim on any frame (Fault Red itself falls to 1.6:1
  /// over a bright one).
  static const errorText = Color(0xFFFF8A80);

  /// Simple Mode's full-screen safety alarm: a deep red over everything, so it
  /// reads as an alarm from across the room ([textPrimary] on it passes 7:1).
  static const alarmScrim = Color(0xE03C0808);

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

  /// Amber warning — HIG-style inline warning (icon + color + text, never
  /// color alone). Shares the hue with [accentBusy] but reads as a distinct
  /// semantic for warning surfaces (e.g. "reachable, but unverified").
  static const accentWarning = Color(0xFFFFB300);

  // Selection / buttons
  static const selectionBg = Color(0xFF1976D2);

  /// [selectionBg] at 25 %: a selected or current item on a translucent
  /// panel (Simple Mode's rail buttons, Autopilot's step in hand).
  static const selectionTint = Color(0x401976D2);
  static const selectionFg = Color(0xFFFFFFFF);
  static const buttonPrimary = Color(0xFF1565C0);
  static const buttonSecondary = Color(0xFF424242);
}
