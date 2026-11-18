# Post-mortem: <short title>

| | |
|---|---|
| Date | YYYY-MM-DD |
| Incident | INC-… (ticket from `./lab.ps1 incident`) |
| Severity | SEV-1 / SEV-2 / SEV-3 |
| Duration | from first impact to recovery |
| Author | |

## Summary
Two or three sentences: what happened, who was affected, how it was resolved.

## Impact
- Customers / payments affected (how many, which kind), money at risk, data correctness.
- Error rate / latency during the incident, duration of the degradation.

## Timeline
| Time | Event |
|---|---|
| hh:mm | First impact (from metrics, not from the ticket) |
| hh:mm | Detected (alert / ticket) |
| hh:mm | Investigation steps, hypotheses, evidence |
| hh:mm | Mitigation applied |
| hh:mm | Recovered / verified |

## Root cause
What exactly went wrong and why (technical mechanism, not "human error"). Include the evidence: the
dashboard panel, query, trace or log line that proves it.

## Detection
How was it noticed? How could it have been noticed earlier (which alert / SLO / metric was missing)?

## Resolution and fix
- Mitigation (what restored service).
- Permanent fix (code / configuration change, commit link) and how it was verified (reproduction before/after).

## Prevention / follow-ups
| Action | Type (prevent / detect / mitigate) | Owner | Status |
|---|---|---|---|
| | | | |

## Lessons learned
What went well, what went badly, where we got lucky.
