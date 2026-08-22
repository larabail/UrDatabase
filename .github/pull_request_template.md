<!--
Title this pull request like a commit: kind(scope): imperative summary
e.g. feat(search): fall back to a LIKE query when the FTS index is absent
-->

## What this changes

<!-- What was wrong or missing before, and what this does about it. -->

## Why this way

<!--
The reasoning, and what you rejected. If you weighed two approaches, say which
and why the other lost. If you deliberately left something undone, say so here
rather than leaving it to be discovered.
-->

## How it was tested

<!--
The tests you added, and anything you checked by hand that a test cannot cover
— on Windows, on macOS, against a real catalogue, with no TMDB key configured.
-->

## Checklist

- [ ] Branched off `main`; no commits made directly on `main`
- [ ] `<Version>` in `Directory.Build.props` bumped to match what this changes —
      MINOR for a `feat`, PATCH for a `fix`, none when nothing under `src/`
      changed. See [Versioning](../AGENTS.md#versioning)
- [ ] `dotnet build` is clean
- [ ] `dotnet test` passes, and new behaviour has tests covering it; a bug fix
      has a test that failed before it
- [ ] README.md was updated for any setup, command, architecture, CI, or
      user-facing change
- [ ] No commit carries a `Co-authored-by` trailer
- [ ] No API key, `movies.db`, poster cache or personal file path is committed;
      new settings were added to `appsettings.example.json` with placeholders
- [ ] Commit messages follow the convention in [AGENTS.md](../AGENTS.md)
