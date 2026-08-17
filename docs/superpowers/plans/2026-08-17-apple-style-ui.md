# Apple-Style UI Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Refresh the DeepSeek Harness Control Panel with a light Apple-inspired WinForms visual style while preserving every existing business behavior.

**Architecture:** Keep the existing single-file WinForms application and event handlers. Replace only the `BuildUi` layout and add small self-painted UI helper controls for rounded cards, status indicators, and buttons. The existing state refresh, process, install, update, and log methods remain the source of truth.

**Tech Stack:** C# WinForms, .NET Framework 4.x, System.Drawing, System.Drawing.Drawing2D.

## Global Constraints

- Keep the Windows native title bar and window controls.
- Do not change installation, Node/pnpm detection, Harness process management, update, scan, or log content logic.
- Do not add runtime dependencies.
- Preserve all eight existing buttons, their order, and their event handlers.
- Keep the existing compiler/build script and blue whale application icon.

### Task 1: Replace the Main Layout

**Files:**
- Modify: `src/DeepSeekHarnessControlPanel.cs` in `BuildUi`, `AddButton`, and UI-only helper methods.

- [ ] Set a light gray window background, larger default size, and a five-row responsive layout consisting of the status card, spacing, action bar, spacing, and log card.
- [ ] Build the status card with four aligned rows for install directory, install state, running state, and Harness version.
- [ ] Build the action bar with the same eight buttons and existing event handlers.
- [ ] Build the log card around the existing `logBox`, preserving multiline read-only scrolling and append behavior.

### Task 2: Add Self-Painted Visual Controls

**Files:**
- Modify: `src/DeepSeekHarnessControlPanel.cs` by adding UI helper classes below `Program`.

- [ ] Add a rounded white `AppleCardPanel` with a light border and double buffering.
- [ ] Add a rounded `AppleButton` with blue primary styling for Start, white secondary styling for other actions, and native enabled/disabled behavior.
- [ ] Add a `StatusValuePanel` that draws a green, gray, amber, or blue status dot based on the existing child label text without changing that text.
- [ ] Keep custom drawing limited to appearance; no helper may invoke business operations.

### Task 3: Build and Verify

**Files:**
- Build: `scripts/build.ps1`
- Output: `bin/DeepSeekHarnessControlPanel.exe`

- [ ] Compile with the existing build script.
- [ ] Run `git diff --check` and inspect that changes are limited to UI code plus the plan.
- [ ] Verify the desktop executable hash matches the newly built executable after replacement.
- [ ] Check the application starts and retains native title bar, status display, button enablement, and log scrolling.
- [ ] Commit and push the UI-only change to the private GitHub repository.
