# Onboarding Decisions

- Status: retained as a programmatic bootstrap flow for v1.
- Current behavior: `OnboardingBootstrapService` seeds the entity registry and writes `aaak_entities.md` plus `critical_facts.md`.
- Reason: the bootstrap files and seeded registry are still useful for parity, but the interactive first-run CLI experience is not required for the initial cutover.
