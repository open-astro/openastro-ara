### Fixed

- **Guide camera back is announced** (guider, #1871): when the guide camera drops with no sequence running, Ara now posts "Guide camera back" once the guider reconnects it. Before, the critical "Guide camera dropped" notice stayed alone. The drop notice now says "lost its guide camera and is reconnecting it" instead of the raw `equipment_disconnected` token.
