### Fixed (plan run)

- **One autofocus per plan, after the guider starts** (#1875): a plan with a guider now runs slew, cool, Start Guiding, autofocus, first light, so the autofocus runs on a cooled sensor and the temperature trigger measures from it instead of firing again right after Start Guiding. A plan without a guider still waits for the cool before its autofocus.
- **Start Guiding connects the guider's equipment and retries once before it pauses** (#1875): a refused start is tried again once, and the guider's camera and mount are connected first, so the run no longer pauses on "all equipment must be connected first".
- **Repeated Pause taps leave one error pill** (#1875): a failed Pause tapped again while the first error pill is showing no longer adds another.
- **The guider's progress reads "Guider", not PHD2** (#1875): "Start guiding · PHD2 - Calibrating" now reads "Start guiding · Guider - Calibrating" in the plan status.
