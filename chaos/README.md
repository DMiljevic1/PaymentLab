# ⚠️ Spoilers

This folder contains the incident scenarios used by `./lab.ps1 incident | hint | reveal | resolve`
(`gm.ps1` game master, `scenarios.ps1` catalogue, `lib.ps1` helpers, extra traffic in `k6/`, SQL in `sql/`).
Reading it tells you the answers. Runtime state is kept in `.state/` (gitignored).

`./chaos/verify.ps1` runs every scenario: inject → wait until the symptom is visible → resolve → smoke test.
