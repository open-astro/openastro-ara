# Uncropped Album Art on the Plan target card

Simple Mode's Plan window shows the picked target's Album Art in a 40 × 40 px
square, cropped top and bottom (`BoxFit.cover`). Ara keeps that square and does
not show the Art uncropped there.

## Why this is out of scope

The maintainer compared three layouts in a mock on 2026-11-16, each with the
real Art and the longest titles after #1966 ("Orion Nebula + Running Man
(M 42)"):

- **A. The 40 × 40 square (kept).** Cropped, but every row in the sheet keeps
  the same art width as the 40 × 40 placeholder for objects without Art, so
  titles line up from row to row.
- **B. The Album aspect, 29 × 40, whole picture on black**, as the Sky Map's
  pick card does. The Art files are 560 × 672 (5 : 6), not the card's
  2.5 : 3.5, so this leaves thin black bars at the sides.
- **C. The picture's own aspect, 33 × 40.** Whole picture, no bars.

The card is a 40 px reminder of what was picked, not a place to look at the
picture: the Sky Map card and the Sky Album show the whole Art. An even row
beats the few pixels B and C give back.

## Prior requests

- #1960: "Plan target card: show the Album Art uncropped, at the Album aspect"
