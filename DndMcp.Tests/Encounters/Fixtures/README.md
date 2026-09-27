# SRD table excerpts

Verbatim copies of the tables the encounter maths is checked against, so the parity tests read the SRD's own text
rather than a second hand-typed copy of it. Each file is a byte-for-byte line range of the SRD markdown in the
serving-solid-characters repository (`Docs/`, commit `deba09ef413f8ca4da172e0d7355b01490a7a413`);
`SrdTableFixtureTests` re-checks every range against that repository when it is checked out beside this one.

| File | Source | Lines |
|---|---|---|
| `srd52-xp-budget-per-character.md` | `dndsrd5.2_markdown-main/src/09_GameplayToolbox.md` | 607–630 |
| `srd52-experience-points-by-challenge-rating.md` | `dndsrd5.2_markdown-main/src/11_Monsters.md` | 154–191 |
| `srd52-proficiency-bonus-by-challenge-rating.md` | `dndsrd5.2_markdown-main/src/11_Monsters.md` | 197–208 |
| `srd51-experience-points-by-challenge-rating.md` | `dnd.srd.5.1-main/10_Monsters/Monsters.md` | 239–267 |
| `srd51-proficiency-bonus-by-challenge-rating.md` | `dnd.srd.5.1-main/10_Monsters/Monsters.md` | 131–168 |

The SRD 5.1 markdown's XP table skips CR 9–13 and CR 26–30 (the SRD 5.2.1 table has every row), and ends with an
empty row; both are left as the markdown has them.

SRD 5.1 and SRD 5.2.1 are © Wizards of the Coast LLC, licensed under CC-BY-4.0 (see `content/LICENSES/`).
