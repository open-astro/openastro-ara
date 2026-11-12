### Fixed (guiding)

- A GoTo stops Guiding before the mount moves, as Park and Home do; start Guiding again after the slew and pier side is checked again. A sync, a rejected request and a Guider that is only looping are left alone (#1749).
- The daemon's RMS leaves out frames taken while the Guider settles, so Simple Mode shows the same kind of figure as the Full UI strip (#1749).
- Polar Align stops a looping Guide Camera at the start and carries on; a loop that will not stop fails the start and says so (#1749).
- Guiding settings: aggressiveness reaches the Guider under the name and scale the current algorithm has, or is skipped when it has none. A refused setting is logged and listed in Guider settings and Tune Guiding, and no longer raises an Equipment error. Out-of-range values are refused at save and left out of the push; mass change is a percentage and variable delays are whole seconds (#1748).
- A guide pulse no longer reads as lost tracking on a mount that reports tracking off for the length of the pulse (#1732).
- The polar alignment residual from guiding is marked unreliable (greyed) when it is more than 2′ above the last Polar Align result, after its uncertainty, and the guiding strip says "above Polar Align" rather than "noisy". Before, Dec pulses the mount did not execute read as drift, and a 0.9′ alignment showed ≥45′ (#1731).
- Polar Align and the guide view treat a 0×0 frame size from the Guider as unknown. Before, the log named a 0×0 frame, the check never asked again, and clicking a star in the guide view was refused as outside a 0×0 frame (#1730).
