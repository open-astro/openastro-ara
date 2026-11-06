# Deep offline star catalogue

The planetarium's bundled star layer stops at magnitude 7. Ara does not ship
a deeper offline catalogue (mag 12 to 14) to replace it.

## Why this is out of scope

The star layer exists to orient the user: find the target, see what's around
it, check the framing. Mag 7 is enough for that. Fainter stars add detail the
planning workflow does not use, and the cost comes up front, in a larger
download every user pays for.

The online Gaia DR2 HiPS layer was tried. It rendered correctly, but it
destabilised the offscreen renderer and was reverted. An offline copy of the
same deep tiles would remove the network dependency but keep the render load,
and Norder tiles to mag 14 add hundreds of megabytes.

Plate solving does not depend on this layer. ASTAP uses its own star
databases, which the Data Manager installs and matches to the rig's field of
view.

## Prior requests

- #1153: "Deep offline star catalogue (mag ~12–14) to replace the reverted online Gaia layer (§36)"
