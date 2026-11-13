### Added

- **Rig usage graphs** (Simple Mode, #1582): Settings › Rig now draws Storage, CPU and Memory as graphs over the last hour or the run so far, each with its current figure, a one-line trend, a Show table view and a dashed warning line (storage under 10 % free, CPU over 85 %, memory over 3.4 GB). The rig samples these every 15 seconds and keeps 12 hours (new `GET /api/v1/server/usage-history`), so the graph is full after the iPad sleeps; a daemon restart starts it empty, and an older daemon keeps today's figures.
