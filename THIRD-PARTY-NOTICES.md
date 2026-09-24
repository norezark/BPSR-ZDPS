# Credits and license notices

This is the unofficial **ZDPS Illusion Energy** fork maintained at
https://github.com/norezark/BPSR-ZDPS.

The combined fork is distributed under **GNU AGPL version 3 only
(AGPL-3.0-only)**. The complete license is in [LICENSE](LICENSE).
It is provided without warranty; see sections 15 and 16 of that license.
You may redistribute and modify this fork under those terms.

## BPSR-ZDPS

- Original project: https://github.com/Blue-Protocol-Source/BPSR-ZDPS
- Base: v0.1.7.5, commit `cfeb58c0acc85bc17181b413b9e50b0b26c15c5d`
- Copyright (c) 2025 Blue-Protocol-Source
- Original license: MIT, preserved verbatim in
  [LICENSES/BPSR-ZDPS-MIT.txt](LICENSES/BPSR-ZDPS-MIT.txt)

The upstream source history and its notices are retained. The original
ZDPS portions remain available under their MIT terms. This fork is not an
official release of, or an endorsement by, the upstream ZDPS project.

## resonance-logs-cn

- Original project: https://github.com/fudiyangjin/resonance-logs-cn
- Version: v0.2.4, commit `bd71d2dfd3c7289e6398c4cd042f4357d4f35721`
- Credit: fudiyangjin and the resonance-logs-cn contributors
- Original license: AGPL-3.0-only (as declared in its `package.json` and README)
- Its license text is reproduced without changes in this fork's [LICENSE](LICENSE).

The factor templates, energy costs and original Japanese display data under
`BPSR-ZDPS/Data/FactorEnergy/` originate from this project. The event and
counter behavior has been ported to C# in `BPSR-ZDPS/Features/FactorEnergy/`.
The exact source paths and revision are recorded in
`BPSR-ZDPS/Data/FactorEnergy/provenance.json`.

## Changes in this fork

Modified on **2026-09-25** for the first public release
`v0.1.7.5-illusion.1`:

- Integrated Illusion Energy tracking into the ZDPS capture lifecycle and UI.
- Added factor delta parsing, per-item thresholds, independent settings and tests.
- Fixed synchronization when optional attributes are omitted.
- Reviewed and corrected Japanese names and summaries for 123 display entries.
- Added fork identification, distribution notices and reproducible packaging.

Japanese display changes are documented in [JAPANESE-NAMES.md](JAPANESE-NAMES.md).
The other libraries and assets inherited from ZDPS retain their respective
licenses and notices; the combined-work license does not replace those notices.

## Corresponding source

Every binary release is accompanied by a source ZIP for the same commit at
https://github.com/norezark/BPSR-ZDPS/releases.
Build instructions and upstream merge instructions are in
[README.FactorEnergy.ja.md](README.FactorEnergy.ja.md).
`BUILD-INFO.json` in the binary ZIP identifies its source commit.
