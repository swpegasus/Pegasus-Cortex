# SQL and Relational Database Tenets

These tenets specialize `DATA-*`, `SEC-006`, `CONC-*`, and `PERF-*` for relational databases.

## SQL-001 — Parameterize data values

**Rule:** Never concatenate untrusted data into SQL text.

**Practice:** Use parameters for values and a strict allowlist or trusted query builder for dynamic identifiers.

**Boundary:** Parameters cannot represent table names, column names, or arbitrary syntax.

## SQL-002 — Enforce durable invariants in the database

**Rule:** Use appropriate primary, foreign-key, unique, check, and nullability constraints for facts the database owns.

**Practice:** Keep application validation for user feedback while relying on constraints against concurrent or alternate writers.

**Boundary:** Cross-database or external invariants require a broader consistency design.

## SQL-003 — Define transaction and isolation semantics

**Rule:** Choose boundaries and isolation according to the invariant and concurrency anomalies that must be prevented.

**Practice:** Keep transactions bounded and handle deadlock, conflict, or serialization failure only through safe retry semantics.

**Boundary:** A transaction does not include external side effects unless a larger workflow reconciles them.

## SQL-004 — Evolve schemas compatibly

**Rule:** Use versioned migrations and coexistence sequencing when old and new application versions overlap.

**Practice:** Prefer additive expansion, data migration, observation, and later contraction for online change.

**Boundary:** Destructive or offline changes require explicit backup, downtime, and rollback limits.

## SQL-005 — Make query results explicit

**Rule:** Select required columns, define ordering when it matters, and handle null, duplicate, time, and decimal semantics deliberately.

**Practice:** Avoid relying on physical row order or implicit conversions.

**Boundary:** Exploratory ad hoc queries may use broader projection when they are not application contracts.

## SQL-006 — Optimize from representative plans

**Rule:** Index and reshape queries using measured execution plans and realistic data distributions.

**Practice:** Account for write cost, storage, selectivity, parameter behavior, and maintenance.

**Boundary:** An index that helps one query may harm the overall workload; measure system effect.

## SQL-007 — Prevent accidental unbounded work

**Rule:** Reads and modifications require operation-appropriate limits, validated predicates, timeout behavior, and cost bounds.

**Practice:** Page bounded reads; for modifications use expected affected-row counts, bounded key or range batches, timeouts, and resumable progress, and review plans at expected scale.

**Boundary:** Administrative batch operations may be large when scheduled, monitored, resumable, and isolated.

## SQL-008 — Separate database roles

**Rule:** Migration, administration, application read, application write, reporting, and backup authority should be granted independently.

**Practice:** The routine application identity should not run as schema owner or unrestricted administrator.

**Boundary:** Small local applications may combine roles when the threat model and data consequence justify it.

## SQL-009 — Keep ORM behavior visible

**Rule:** Abstractions must not hide transaction scope, generated SQL, query count, concurrency tokens, loading behavior, or migration semantics.

**Practice:** Inspect generated queries and database outcomes for important paths and performance changes.

**Boundary:** An ORM remains valuable when it reduces boilerplate without obscuring owned invariants.

## SQL-010 — Reset pooled session state

**Rule:** Transactions, isolation changes, temporary objects, role changes, session variables, and command settings must not leak to the next borrower of a pooled connection.

**Practice:** End or roll back owned transactions, restore changed session settings, and use driver-supported reset behavior before returning the connection.

**Boundary:** Disposing a pooled connection returns it to the pool; it does not make undefined session ownership safe.
