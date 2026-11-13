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

  /// Text-button labels on Simple Mode's panels and tiles: [accentInfo]
  /// lightened so it reads at 4.5:1 on a tile (#383838) as well as the scrim
  /// (Info Blue itself is 4.4:1 there, the button primary 2:1).
  static const linkText = Color(0xFF64B5F6);

  /// The tile fill behind Simple Mode's fields, presets and mode tiles
  /// (DESIGN.md `tile-fill`). Tiles always sit on a panel's 90 % scrim
  /// ([overlayScrim]), and that is what makes [textSecondary] on them read at
  /// 4.5:1 over a white frame (4.7:1); the tile alone over white is 1.2:1.
  static const tileFill = Color(0x8C383838);

  /// Simple Mode's full-screen safety alarm: a deep red over everything, so it
  /// reads as an alarm from across the room ([textPrimary] on it passes 7:1).
  static const alarmScrim = Color(0xE03C0808);

  /// Every Emergency Stop's fill: Fault Red darkened so its white label reads
  /// at 5:1 (Fault Red gives 4.2:1), in both densities since #1569.
  static const emergencyStop = Color(0xFFD32F2F);

  /// The Emergency Stop's label on [emergencyStop] (5:1).
  static const onEmergencyStop = Color(0xFFFFFFFF);

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

  // Sky Album tiers: a category hue per Card tier, each 5:1 or better on
  // Panel Grey and Night Floor (the tier name is written in it).
  static const tierBronze = Color(0xFFCD853F);
  static const tierSilver = Color(0xFFC0C0C0);
  static const tierGold = Color(0xFFFFD54F);

  /// Text on a tier-coloured banner: 7:1 or better on every tier.
  static const onTier = Color(0xFF000000);

  /// Behind a Card's Art, the night sky the picture sits on.
  static const albumArtBackdrop = Color(0xFF000000);

  /// A missing Card's Placeholder silhouette: 3.4:1 on [bgPanelAlt], an
  /// informative graphic, and dimmer than a collected Card's.
  static const albumPlaceholderMissing = Color(0xFF808080);

  // Selection / buttons
  static const selectionBg = Color(0xFF1976D2);

  /// [selectionBg] at 25 %: a selected or current item on a translucent
  /// panel (Simple Mode's rail buttons, Preflight's step in hand).
  static const selectionTint = Color(0x401976D2);
  static const selectionFg = Color(0xFFFFFFFF);
  static const buttonPrimary = Color(0xFF1565C0);
  static const buttonSecondary = Color(0xFF424242);

  // Brand: the app icon's palette, for the OpenAstro Ara mark and Simple
  // Mode's no-frame wallpaper only (#1602); never for state or emphasis.
  static const brandNavyTop = Color(0xFF272C4D);
  static const brandNavyMid = Color(0xFF171B3B);
  static const brandNavyBottom = Color(0xFF070914);
  static const brandLineStart = Color(0xFF4FC3F7);
  static const brandLineEnd = Color(0xFF7C8CF8);
  static const brandGlow = Color(0xFF8FA8FF);
  static const brandStarHole = Color(0xFF0B0F24);

  // #1552 — colours that lived as literals outside lib/theme.

  /// Paints, unseen (1/255), so a full-screen tap layer takes clicks over a
  /// native view on macOS.
  static const unseenPaint = Color(0x01000000);

  /// A 35 % shade behind an open popover; taps on it close the popover.
  static const popoverShade = Color(0x59000000);

  /// Emergency Stop's hold fill: darkens the button, keeping HOLD at 7.7:1.
  static const holdFill = Color(0x40000000);

  /// The near-black behind a camera frame in focus and guide cards.
  static const frameBackdrop = Color(0xFF07080B);

  /// A hairline divider drawn over a frame or a dark card (12 % white).
  static const frameDivider = Color(0x1FFFFFFF);

  /// Captions and quiet buttons drawn over a camera frame.
  static const onFrameText = Color(0xFFC8C8C8);
  static const onFrameTextQuiet = Color(0xFF9A9A9A);

  /// The Bahtinov readout's chart line and points.
  static const bahtinovChart = Color(0xFF90CAF9);

  /// A thumbnail's caption gradient, its unselected tick and its hover wash.
  static const thumbnailCaptionShade = Color(0xB3000000);
  static const thumbnailTickShade = Color(0x66000000);
  static const thumbnailHover = Color(0x14FFFFFF);

  /// The notification centre's drop shadow.
  static const panelShadow = Color(0x66000000);

  /// The crash screen, drawn without the theme.
  static const errorFallbackBg = Color(0xFF1E1E22);
  static const errorFallbackText = Color(0xFFB0B0B8);

  /// The flat panel drawing: its face, its light and its cover.
  static const flatPanelFace = Color(0xFF111111);
  static const flatPanelLightCentre = Color(0xFFFFFBE8);
  static const flatPanelLightEdge = Color(0xFFB8B29A);
  static const flatPanelLid = Color(0xFF303030);
  static const flatPanelSeam = Color(0xFF242424);

  /// The all-sky dome drawing: its lower ring, its shells, plinth and bolt.
  static const domeLowerLight = Color(0xFF808080);
  static const domeLowerDark = Color(0xFF4A4A4A);
  static const domeShellLight = Color(0xFFA8A8A8);
  static const domeShellDark = Color(0xFF6A6A6A);
  static const domePlinth = Color(0xFF8A8A8A);
  static const domeBolt = Color(0xFFD0D0D0);

  /// The dome drawing's slit where it is now.
  static const domeSlit = Color(0xF26E6E6E);

  /// The Filter wheel window's wheel (#1676): the knurled rim's two bands, the
  /// face inside it, and each filter's glass (a cell's tint is content, like
  /// the image, never a state) with its label colour.
  static const wheelRimLight = Color(0xFF454545);
  static const wheelRimDark = Color(0xFF333333);
  static const wheelFace = Color(0xFF2C2C2C);
  static const glassPlain = Color(0xFF262626);
  static const glassClear = Color(0xFFCFD6E2);
  static const glassUvIr = Color(0xFFB9C4CF);
  static const glassRed = Color(0xFFB3342A);
  static const glassGreen = Color(0xFF2F7D50);
  static const glassBlue = Color(0xFF2B5FB8);
  static const glassHa = Color(0xFF7C1622);
  static const glassOiii = Color(0xFF15807A);
  static const glassSii = Color(0xFF5A1A3A);
  static const glassDualBand = Color(0xFF6A4596);
  static const glassDark = Color(0xFF0B0B0B);
  static const glassDarkLabel = Color(0xFFBBBBBB);
  static const glassGloss = Color(0x61FFFFFF);

  /// Luminance glass: the visible spectrum around the cell.
  static const glassSpectrum = SweepGradient(
    startAngle: 0,
    endAngle: 6.283185307179586,
    transform: GradientRotation(3.49),
    colors: [
      Color(0xFFFF3B3B),
      Color(0xFFFF9A2E),
      Color(0xFFFFE23B),
      Color(0xFF3BD16F),
      Color(0xFF2FB8E8),
      Color(0xFF4B5CFF),
      Color(0xFFA54BFF),
      Color(0xFFFF3B3B),
    ],
  );

  /// A sequence instruction's category colour in the sequencer.
  static const instructionCamera = Color(0xFF64B5F6);
  static const instructionFilterWheel = Color(0xFFBA68C8);
  static const instructionFocuser = Color(0xFF4DB6AC);
  static const instructionRotator = Color(0xFF7986CB);
  static const instructionTelescope = Color(0xFFFFD54F);
  static const instructionGuider = Color(0xFF81C784);
  static const instructionSwitch = Color(0xFFFF8A65);
  static const instructionCalibration = Color(0xFFA1887F);
  static const instructionUtility = Color(0xFF90A4AE);

  // #1552 follow-up — the Material Colors.* constants used outside lib/theme.

  /// White text and icons on a coloured fill (a red Remove or Stop button,
  /// a selected tick, a badge).
  static const onFill = Color(0xFFFFFFFF);

  /// Black text on a bright green or amber fill (Start, a selected chip).
  static const onBrightFill = Color(0xFF000000);

  /// The black behind a frame or a sky preview, letterboxing included.
  static const imageBackdrop = Color(0xFF000000);

  /// Text over a frame or photo: full and quiet, with its shadow.
  static const onImageText = Color(0xFFFFFFFF);
  static const onImageTextQuiet = Color(0xB3FFFFFF);
  static const onImageShadow = Color(0xFF000000);

  /// A dark chip or bar laid over a frame (55 % black).
  static const onImageChip = Color(0x8C000000);

  /// The sky preview's mask outside the frame, its outline and the dim ring.
  static const previewMask = Color(0x73000000);
  static const previewMaskStrong = Color(0x99000000);
  static const previewOutline = Color(0xB3FFFFFF);

  /// Modal barriers: the command palette, a full-screen frame, a card sheet,
  /// the notification centre.
  static const barrierPalette = Color(0x8A000000);
  static const barrierFrame = Color(0xDD000000);
  static const barrierSheet = Color(0x61000000);
  static const barrierLight = Color(0x1F000000);

  /// A raised panel's or a dragged row's drop shadow.
  static const dragShadow = Color(0x73000000);

  /// A dim layer over a frame while a panel sits on it (26 % black).
  static const frameDim = Color(0x42000000);

  /// The Settings search hit's outline.
  static const searchHighlight = Color(0xCCFFEB3B);

  /// A poor frame-quality bar.
  static const qualityPoor = Color(0xFFFF5252);

  /// A star in the wallpaper and the Ara mark's bright star.
  static const starlight = Color(0xFFFFFFFF);
}
