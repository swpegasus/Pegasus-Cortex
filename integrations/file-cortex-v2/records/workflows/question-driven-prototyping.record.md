# Question-Driven Prototyping

## PROTO-001 — Name the question before building

**Rule:** A prototype exists to answer one explicit uncertainty about logic, state, interaction, feasibility, or presentation; that question determines the prototype's shape.

**Practice:** State the question, the observation that would answer it, and the assumptions being held constant before choosing a terminal harness, UI variation, model, or other exploratory artifact.

**Boundary:** A production increment, migration rehearsal, performance benchmark, or compliance test may need prototype-like learning while retaining stronger engineering and evidence requirements.

## PROTO-002 — Keep prototypes isolated and visibly disposable

**Rule:** Prototype code and state must be clearly marked, bounded, and separated from production authority so exploratory shortcuts cannot become accidental dependencies.

**Practice:** Prefer in-memory or scratch state, one obvious run path, minimal dependencies, and names or locations that identify the artifact as temporary.

**Boundary:** When persistence, deployment, security, or integration is the question being tested, exercise that boundary in an isolated fixture with explicit cleanup and no production credentials or data.

## PROTO-003 — Expose the state being evaluated

**Rule:** A prototype should make the relevant state, transition, output, or variation directly observable after each meaningful action.

**Practice:** Print, render, or otherwise surface the complete bounded state needed to compare alternatives and identify what changed.

**Boundary:** Do not expose secrets, private data, or unrelated internals merely to make a prototype convenient.

## PROTO-004 — Optimize for learning, not polish

**Rule:** Add only the error handling, structure, instrumentation, and fidelity required to answer the prototype's question reliably.

**Practice:** Avoid speculative abstractions, production hardening, and unrelated features while preserving enough determinism and safety to trust the observation.

**Boundary:** If the artifact will be retained, shipped, or used operationally, it has crossed out of prototype status and must meet the owning product's normal implementation and verification gates.

## PROTO-005 — Retain the answer, then delete or absorb the artifact

**Rule:** Once the question is answered, preserve the bounded conclusion and supporting evidence, then remove the prototype or deliberately incorporate the validated behavior into the canonical implementation.

**Practice:** Record what was learned, which alternative was selected, remaining uncertainty, and where the production follow-up belongs; do not leave an abandoned parallel path in the repository.

**Boundary:** Retain the prototype itself only when it remains an explicitly owned repeatable experiment, sample, or regression harness with a defined lifecycle.
