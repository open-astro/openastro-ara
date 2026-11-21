### Fixed (Server)

- **The daemon never asks for a password through polkit**: every `systemctl` call the daemon makes (guider start, restart and status; End Night poweroff; storage and update helpers) now passes `--no-ask-password`. On the rig the shipped polkit rules allow these calls without a prompt, so nothing changes there. On a Linux desktop with a polkit agent, a daemon that could not reach the guider popped a password dialog for `systemctl start openastro-guider`; it now gets a refusal and carries on.

### Fixed (tests)

- **A test run no longer starts or restarts the guider unit**: the `SystemctlGuiderProcessSupervisor` tests ran a real `systemctl start` and `systemctl restart openastro-guider`, which popped a polkit password dialog on every run on a Linux desktop. They now record the argv through a process seam. Every in-process daemon host (`AraHost` and each `Program.BuildApp` test) replaces the guider supervisor and the poweroff starter with fakes (`TestHostSeams`), and two guard tests fail if Program.cs wiring ever escapes the swap.