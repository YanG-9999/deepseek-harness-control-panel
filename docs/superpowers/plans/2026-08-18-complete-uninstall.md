# Complete Uninstall Implementation Plan

**Goal:** Add a confirmed complete-uninstall action that removes Harness-owned files and user data without touching system-wide Node tooling.

**Architecture:** The UI keeps the existing WinForms layout and adds one uninstall button wired to a dedicated asynchronous workflow. A pure target planner produces exactly three deletion targets: the configured Harness install root, the default Harness home under the current user's profile, and the manager settings directory. A safety guard validates every target before recursive deletion.

**Safety Rules:**

- Stop Harness before deleting anything.
- Delete only the configured Harness root when it still contains the Harness source marker.
- Delete only the current user's `.dsh` home and the manager's own settings directory.
- The private `.dsh-runtime` Node/pnpm directory is removed as part of the Harness root.
- Never execute a global Node, npm, or pnpm uninstall command.
- Abort with a clear error if any target cannot be deleted.

**Verification:**

- Unit tests cover stale process IDs and the three uninstall targets.
- The existing build script must compile the desktop executable.
- The desktop executable must be replaced only after the manager process is closed.
