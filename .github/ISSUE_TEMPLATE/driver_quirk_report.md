---
name: Driver quirk report
about: An Alpaca device/driver misbehaves with Ara (works elsewhere, or violates the spec)
title: 'quirk: <device> — <one-line symptom>'
labels: driver-quirk
assignees: ''

---

**Device + driver**
- Device model:
- Alpaca driver + version (and the server it runs on — e.g. AlpacaBridge, ASCOM Remote, native):

**Symptom**
What Ara does with this device vs what should happen. Include the exact error text or the
diagnostics-panel entry if there is one.

**Conformance**
If you can, run [ConformU](https://github.com/ASCOMInitiative/ConformU) against the device and
attach the report — it usually pinpoints whether the driver or Ara is off-spec.

**Notes for triage**
Confirmed driver-side bugs get routed upstream to the driver/bridge project; Ara-side
workarounds for popular devices are considered case-by-case.
