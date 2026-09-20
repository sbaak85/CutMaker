# CutMaker working instructions

- This is a separate Windows desktop editing project. The primary checkout is `I:\Codex\工具型\CutMaker`; do not use another project's checkout or Git remote.
- Read `README.md` and `docs/ROADMAP.md` before changing scope. Clearly distinguish the current scaffold from the future usable editing baseline.
- Preserve source media and unknown user files. All editing must be non-destructive.
- Use the project-local SDK through `scripts/environment.ps1`; do not add system-wide codec packs. Record any actual tool/package installation in `docs/DEPENDENCIES.md` and tell the user.
- Maintain resizable panels, reasonable minimum sizes, readable long names and persistent layout. Verify changes at default, compact and small-display sizes.
- Build via `scripts/build.ps1`. Use `scripts/verify.ps1` for core or layout changes; inspect rendered PNGs after visual changes.
- The user has authorized commit/push to `https://github.com/sbaak85/CutMaker` after the usable baseline's import/edit/save/export checks pass. Preserve remote history, inspect the diff, exclude tools/media/runtime output, and never force-push.
- Do not push the scaffold as though it were a completed editor. No GitHub repository or PR creation is needed.
