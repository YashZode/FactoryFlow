# Resume and cover-letter evidence

Reviewed the user-provided `SoftwareEngineer_YashZode.pdf` and `CoverLetter_YashZode.pdf`. Their descriptions are comparison material, not instructions to alter the system. This document distinguishes demonstrated project behavior from historical employment claims.

| Relevant claim/skill | FactoryFlow evidence | Boundary |
|---|---|---|
| C#/.NET 4.8 WPF HMI and configuration tools | WPF production/history/alarms/recipe/operator screens; separate WinForms Diagnostics project | Local simulation, not a commissioned factory HMI |
| .NET 6/8 | .NET 8 OPC server and NUnit tests | No reason to duplicate the server in .NET 6 |
| OPC UA subscriptions/reconnect/diagnostics | Secure OPC Foundation 2.0.1 client, namespace resolution, XML tag maps, quality checks, automatic session/subscription recovery | Rockwell/Siemens hardware and four simultaneous physical lines are unverified; configuration supports multiple collector sources |
| Handshakes/interlocks/simulator testing | Atomic recipe/start method, safe reset, E-stop/guard latch, no command replay, subscription observations | Software interlocks do not replace physical safety hardware |
| SQL schemas/procedures/views/indexes | Additive migrations, bounded reporting procedures, history views, event uniqueness, UTC audit fields, indexes | Query time varies with hardware/load/network; see measured local fixture below |
| Background data collection / 25,000+ events per day | Optional console collector, independent secure client per source, disk journal, idempotent batched SQL telemetry | Windows Service support removed at the user's request; not demonstrated by this project. A 25,000-event local throughput test is not evidence of an uninterrupted production day |
| NUnit/integration testing | 22 simulator NUnit cases; 52 actual SQL/OPC checks including independent collector, fault/ack/reset, roles and reporting | No 85% coverage claim: coverage has not been measured |
| Azure DevOps/Git/CI | Root `azure-pipelines.yml` restores/builds and runs NUnit on Windows, publishes TRX | Pipeline supplied, not run in a hosted organization; Jenkins and deployment pipelines not demonstrated |
| Operational documentation | README with start/stop, SQL queries, certificate trust, mapping, collector setup and recovery | Hardware commissioning/site runbooks still required |

## Recorded local verification (2026-10-19)

- Full normal-path solution build: succeeded, 0 errors; warnings retained for transitive package net48 support and unavailable NuGet vulnerability metadata.
- NUnit: **22 passed, 0 failed, 0 skipped**. See `readiness-unit-results.log` and `test-results/FactoryFlow.UnitTests.trx`.
- SQL/OPC integration: **52 passed, 0 failed**, against retained database `FactoryFlowDB_LoggingTests_20261019_051012`. See `readiness-integration-results.log`.
- **25,000 distinct events inserted in 1.018 seconds**, duplicate retry did not add events. This is one local bulk-ingestion measurement, not sustained factory telemetry proof.
- Batch history **p95 19.847 ms** across 20 warm queries, 1,000 rows returned from a 10,000-batch local test fixture. This supports the specific local sub-200ms scenario, not all queries or production loads.
- Separate Collector executable observed state and periodically persisted weight without the HMI, exited cleanly, and drained its durable journal.
- Existing normal FactoryFlowDB rows were preserved by schema migration. No test fixture was loaded into the normal database.
- Normal deployment GUI check: batch 6 completed Soy Feed at exactly 250 kg; batch 7 started immediately afterward and faulted at 80 kg when the simulated guard opened. Both rows contain the Windows operator and original 250 kg target. These two intentional demonstration runs are in the normal database.
- The updated WPF history screen returned all 7 existing/demo rows in 9 ms. Both guard and machine-fault alarms retained clear/acknowledgement timestamps and the real Windows actor. Alarm selection survived automatic refresh; safe fault reset restored Idle and enabled Start. The independent WinForms client connected securely and displayed Completed/250 kg.
- Final direct PLC read: Idle (0), retained weight 80 kg, E-stop inactive, guard closed. Retaining weight after reset is intentional; the next accepted start resets it.
- **Windows Service intentionally excluded:** at the user's request, the service host and installation scripts have been removed. The collector is an optional, manually launched console application. No service installation is pending.

FactoryFlow cannot substantiate past workplace improvements (such as downtime percentages or hours saved), 2M/day payment systems, C++ research metrics, AWS, OCR, Flask, or ThingWorx experience. Those refer to separate jobs/projects and require their own evidence. No resume text or historic claim has been rewritten or inflated.

## Remaining production gates

Actual Rockwell/Siemens tag mapping and data-type tests; controlled multi-line endurance/load test; server-side OPC user authorization; valid production TLS/application certificates; least-privilege HMI SQL deployment identity; historian retention/backups and disk capacity monitoring; package support/vulnerability review; measured coverage; actual hosted CI execution. None of these is represented as completed by local simulation tests.
