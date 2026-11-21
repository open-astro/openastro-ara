### Fixed
- **The bedtime prompt waits for your site's twilight line** (#2147): the Preflight "go to bed" card used a fixed -12° sun altitude to decide it was dark, so with the default astronomical twilight (-18°) it appeared too early, and with civil twilight (-6°) too late. It now uses the twilight setting of your site, as the Tonight toggle does.
