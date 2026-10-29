# LaunchReel Studio: build specification

The accepted product is a local Windows advertising studio for applications owned by the user. The first full release requires live app interaction in 3D, recorded demonstrations, independent UI layers, studio and cinematic rendering, a guided editor, and pasted AI storyboards. FlyShelf WPF is the primary reference application. WinUI 3, Android, and web applications require their own tested adapters.

## Invariants

- Real app logic owns behavior. The studio does not fabricate feature outcomes.
- Captured media is immutable. Projects copy imported assets and store edits separately.
- Offline export consumes a frozen recording, never a changing live source.
- Camera state and timeline state are evaluated from absolute time, independently of preview frame rate.
- A bridge exists only in an explicitly enabled development build and authenticates its local client.
- Capture and input are separate capabilities. Unsupported operations are reported.
- Personal clipboard history and device pairing data must never be replaced or seeded for demonstrations.
- AI storyboards contain data and asset references, never executable code.

## Architecture

C#/.NET 10 WPF owns project storage, operating system access, dialogs, capture, and child processes. A local WebView2 surface hosts a Three.js editor. FFmpeg processes media and encodes frame-based output. Blender provides optional offline cinematic rendering. Framework bridges expose named elements and bounded actions. The application works without cloud services.

## Release acceptance

Verify capture, input mapping under camera transforms, recording, editable scenes, project persistence, storyboard validation, interrupted rendering, and actual media output. Validate each platform adapter independently. Benchmark live latency and rendering speed rather than asserting performance from hardware specifications.

## Current shape (v0.3)

- Launch targets: Start menu desktop apps, Store/packaged apps (activated by app ID, matched by process app ID), `.exe`/shortcut files, Android apps (adb + scrcpy mirror, adb input) and the whole screen.
- One build output: `artifacts\studio`, stamped with a source hash (`build-info.json`); `Start Studio.cmd` rebuilds only when stale.
- Real-app hosting: the app's own top-level window is positioned over App view and kept just above Studio by z-order only. It is never owned by or parented to Studio: cross-process ownership attaches the apps' input queues, and with an app that installs low-level keyboard/mouse hooks (FlyShelf does) any stall then freezes input for the whole PC. All window calls to the app are asynchronous; hide/close/minimize/move are detected by a read-only poll (no hooks into the app). Studio never shows a window the app hid, and release restores position, maximized and topmost state.
- Launching: through the Windows shell, exactly like a double-click; a running app is never started twice. Window scanning runs off the UI thread.
- Resilience: UI errors are logged to `logs\` in the data folder and shown without closing Studio; a crashed editor process is reloaded or rebuilt; a fatal error hands the app back first. The preview renders only on change, and capture runs at 6 fps while the app is only being operated.
- Window choice: only visible, uncloaked, on-screen windows; main (taskbar) windows are chosen automatically only when unambiguous; floating windows are captured in place.
- Recording: raw BGRA frames on a wall-clock schedule, so video length equals real time.
- Scene model: devices, stages, finishes, lighting, easing, title layout, fades, screen sections (crops of real pixels), callouts and focus — all validated data shared by the editor, AI scripts, quick commands and the cinematic renderer.
- Prompt control: quick commands (local parser), Studio scripts from any AI chat (storyboard, edits, demo), demos performed on the real app through UI Automation and real input, with interference stops and an app-only click guard.
