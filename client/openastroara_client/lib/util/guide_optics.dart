/// §63.19 guide-setup-type helpers — pure math, no Flutter imports.
///
/// An off-axis guider (OAG) sits behind the SAME optical train as the main
/// camera, so its guide focal length is not user-entered: it is the main
/// telescope's focal length times the reducer/barlow factor.
library;

/// Derived guide focal length (mm, rounded to the nearest integer) for an
/// off-axis guider: `opticsFocalLengthMm × reducerFactor`.
///
/// Returns 0 ("unset" — the daemon keeps the PHD2 profile default) when the
/// main optics aren't configured yet (focal length ≤ 0) or the reducer factor
/// is non-positive (physically impossible; the optics section rejects it too).
int derivedOagGuideFocalLength(double opticsFocalLengthMm, double reducerFactor) {
  if (opticsFocalLengthMm <= 0 || reducerFactor <= 0) return 0;
  return (opticsFocalLengthMm * reducerFactor).round();
}

/// The reducer factor the OAG derivation should use given the base profile's
/// optics section: the base value when it's a usable positive number, else
/// 1.0. Shared by the wizard's preview AND its save mapper so the number the
/// user sees is the number that gets persisted.
double effectiveOagReducerFactor(double? baseReducerFactor) =>
    (baseReducerFactor != null && baseReducerFactor > 0)
        ? baseReducerFactor
        : 1.0;
