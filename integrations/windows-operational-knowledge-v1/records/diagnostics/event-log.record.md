# Knowledge.Windows.Diagnostics.EventLog

Object: Knowledge.Windows.Diagnostics.EventLog
Version: 1.0.0
Triggers: service failure, startup failure, installer failure, crash diagnosis, Windows health UI
Rendered budget: 4096 UTF-8 bytes

## Provider contract

Use a stable product-owned provider/source and bounded event identifiers. Register the source/provider and its message resources through the installer when required by the selected Event Log API. Writing an unregistered source can leave Event Viewer unable to render the intended message correctly.

## Event design

Record operational transitions and failures that help an operator act: component, version, stage, event ID, severity, bounded correlation identity, stable error code, and safe remedy. Do not write secrets, root codes, tokens, private record bodies, raw command streams, or large duplicated logs.

Use informational events selectively. Repeated normal polling, successful routine calls, and high-volume progress belong in bounded application logs or metrics rather than the Windows Event Log.

## Reading

Query by owned provider/channel, event ID, severity, and bounded time window. Return compact summaries and exact event coordinates; load full event details only when needed. Distinguish absence of matching events from failure to access the log.

## Retention and UI

The Event Log is system diagnostic evidence, not the application's primary durable state. A product health UI may show a bounded recent view without ingesting all event messages into Cortex. Keep application logs, crash dumps, metrics, and Event Log roles separate.

## Failure boundary

An event proves only that the emitting path recorded it. Correlate with SCM/process state, application logs, receipts, and a live health check before declaring the current component healthy or failed.

Reference:
- https://learn.microsoft.com/en-us/windows/win32/eventlog/event-sources
