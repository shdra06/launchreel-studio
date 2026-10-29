# Contributing to LaunchReel Studio

Thanks for helping. A few things keep the project trustworthy.

## Ground rules

- **Real apps only.** Features must show what an app really does. Nothing may fake an app's UI or results.
- **Never harm the user's app.**
  - Don't terminate it, launch it a second time, or change its data or settings.
  - Always hand its window back where it was.
- **Data, not code.** Storyboards, plans and AI replies are validated data (`web/project.ts`). Never `eval` them or run them.
- **Local first.** Add no telemetry, network calls or accounts.
- **Be candid.** If a feature doesn't work for some app type or renderer, say so in the UI and in the README's *Known limitations*.

## Getting set up

```powershell
npm ci
npm run check
npm test
dotnet test tests\Studio.Host.Tests
npm run build
```

`scripts\verify.ps1` runs everything, including two desktop checks against the neutral test app in `tests\Studio.Fixture`. They briefly drive that test window, never your own apps. Pass `-SkipDesktop` to skip them.

## Pull requests

- Keep changes focused, and match the style of the surrounding code.
- Add or update tests:
  - editor behaviour: `tests/*.test.ts`;
  - platform-independent host logic: `tests/Studio.Host.Tests`.
- For new app-compatibility work, say in the PR which app types and Windows versions you tested on.

## Reporting bugs

Include:
- your Windows version;
- the kind of app (Win32, WPF, WinUI, UWP, Android…);
- what you expected and what happened.

Studio's log is in `%LOCALAPPDATA%\LaunchReel\logs`.
