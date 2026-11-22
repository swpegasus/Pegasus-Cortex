# Process thread observations

`proc_threads` is an ordinary owner-host, read-only operation. It observes
Windows thread identities for one exact process generation. It does not suspend,
terminate, debug, inject into, or otherwise control a process or thread. It adds
no service dependency or elevation fallback.

The source implementation uses the existing single PAO tool and dispatcher:

```text
pao proc threads pid=42 start=2026-11-20T18:14:00.0000000Z max=64
```

```json
{
  "op": "proc_threads",
  "pid": 42,
  "start": "2026-11-20T18:14:00.0000000Z",
  "max": 64,
  "output": "agent"
}
```

Replace the example PID and process start with the pair returned by process,
application, or terminal inspection. An application ID, terminal session ID,
job ID, and Windows thread ID are different identities.

`pid` and canonical UTC `start` are mandatory. `max` defaults to 64 and is
limited to 1–256. Root authorization, workspace selection, bodies, timeouts,
deferred completion, arbitrary arguments and duplicate selectors are refused.
An ordinary ambient working directory grants no additional access.

The result carries the requested and observed process start and bounded
`threads` rows containing `tid` and the thread's own creation `start`. Both
process and thread identities are queried through retained handles with only
query and synchronization rights. An exited process causes collected rows to
be discarded; an inaccessible process is not described as absent.

Coverage fields describe the observation window:

- `scanned` counts system snapshot entries examined, including other processes.
- `raced` and `unavailable` count matching-PID candidates that disappeared,
  changed ownership, exited, or could not be identified.
- `snapshotExhausted` means the enumerator reached its end. It does not prove
  that all rows existed simultaneously or remain alive.
- `limit` is `results`, `scan`, or null. A verified extra row establishes the
  result limit; the managed scan stops after 65,536 entries.

Windows creates a systemwide Toolhelp thread snapshot before filtering. The
managed limits do not promise a bound on the OS's internal allocation or the
duration of a synchronous native call. Cancellation is checked around calls
and between entries, and owned handles are disposed on every exit.

Each request is a fresh, nondurable snapshot. Reusing a RID cannot substitute an
unrelated saved receipt, acknowledge old work, or create a retained command for
automatic replay. Broker System/application routes and frozen execution refuse
this leaf before claiming work or consuming root authorization.

Focused verification covers injected identity/race/bounds/cleanup contracts,
captured CLI/MCP requests, and isolated Engine/SQLite routing and history. A
separate Windows fixture observed two threads owned by its own test process,
confirmed their creation identities across two observations, and released and
joined them. These checks do not establish deployment, arbitrary protected
process access, or a simultaneous complete inventory.
