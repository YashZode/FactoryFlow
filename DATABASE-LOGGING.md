# SQL Server production logging

The HMI now connects to `localhost\SQLEXPRESS`, database `FactoryFlowDB`, with
Windows integrated authentication. Configuration lives in `FactoryFlow.HMI/App.config`
(and the generated `FactoryFlow.HMI.exe.config`). `FACTORYFLOW_SQL_CONNECTION` can
override it without storing credentials in source. The local-development connection
uses encryption with `TrustServerCertificate=True` for SQL Express's local certificate.
For a deployed SQL instance, install a valid server certificate and use
`TrustServerCertificate=False`. OPC UA certificate validation is unchanged.

## Data recorded

| Table | Recorded information |
|---|---|
| dbo.Batches | Selected RecipeId, LineId, Starting/Running/Completed/Stopped/Fault/Interrupted/Rejected/Unconfirmed status, UTC start/end times, target and final weight, unique batch correlation ID |
| dbo.MachineEvents | Start/Stop requests, actual Running/completion/stop/fault, E-stop/guard states, PLC connection changes, untracked external batches, command failures; event identity, batch correlation ID, UTC observation time |
| dbo.ProductionLines | Most recently observed line/connection status |
| dbo.Recipes | Existing recipe definitions loaded into the HMI; no automatic recipe edits |

`ActualWeight` is the final measured weight for Completed, Stopped or Fault batches.
`CompletedAt` is set only for Completed. `EndedAt` also covers stops, faults and
interrupted monitoring. Interrupted/Unconfirmed outcomes have no invented final
weight. Existing historical rows keep NULL in newly added fields where no recorded
information exists. All database timestamps are UTC observation times, not proof
of the exact physical transition time.

The original database contained one Pending batch (BatchId 1). It was preserved.
The additive migration is `FactoryFlow.Data/Sql/001_ProductionHistory.sql`, embedded
in FactoryFlow.Data and applied on HMI initialization. It adds nullable columns and
filtered unique indexes; it does not drop or clear existing tables.

## Starting and recording a job

1. Close the old HMI and stop the old simulator before rebuilding their normal paths.
2. From `E:\VisualStudioProject\Project2`, build:

```powershell
dotnet build FactoryFlow/FactoryFlow.slnx --no-restore
```

3. Start the simulator:

```powershell
dotnet run --project FactoryFlow/FactoryFlow.PlcSimulator --no-build
```

4. In another PowerShell window start the HMI:

```powershell
.\FactoryFlow\FactoryFlow.HMI\bin\Debug\net48\FactoryFlow.HMI.exe
```

5. Wait for Database to show Connected, sign in with Windows, then click Connect PLC.
   Select an active recipe, then Start Batch. After Completed, Start Batch starts another job.
6. Run `FactoryFlow.Data/Sql/VerifyProductionHistory.sql` in SQL Server Management
   Studio against `localhost\SQLEXPRESS` to inspect actual persisted records.

Soy Feed (250 kg) and Corn Feed (500 kg) both work. The selected target and start
are submitted through one atomic OPC method, and SQL retains that target/name
snapshot even when a Supervisor later changes the recipe.

A start request is committed to SQL before a PLC start is sent. Only an observed
Running state changes that row to Running. Final status and weight must share a
PLC source timestamp, preventing a status update from saving an older weight.
Starting another job first flushes the previous terminal event to avoid racing the
SQL Running row. SQL uses parameterized commands and transactions. Stable EventKey
identities and a unique index make event retries idempotent.

## Failure and shutdown behavior

The HMI displays database status separately from PLC status. A failed SQL connection
blocks new Starts. Stop remains available when the PLC is Running even if SQL is
unavailable. Pending events are flushed by a background worker, not on the UI thread.
Events remain as JSON in `production-outbox` beside the HMI executable until SQL
commits them. Closing/reopening the HMI replays pending records. Do not delete that
folder while records are pending. One recorder holds an exclusive lock on its folder.

Connection loss or HMI shutdown marks its tracked batch Interrupted. It does not
send Stop or claim the physical batch completed. An active marker also permits
interrupted-tracking recovery on startup. If an existing recorded Starting/Running
row has no recoverable marker/outbox, the operator must review that unfinished row;
the application will not silently assign it to a new job.

A job started from UaExpert or another client is recorded as an UntrackedBatchRunning
machine event. It does not invent a recipe or create a misleading batch record.
The HMI must be running to track its own batch ledger. The independent Collector
now records raw state/interlock/connection events and sampled weights without it.
Windows operator mappings, actor audit fields and alarm acknowledgement are added
by migration 002. See README.md for the current screens and service setup.

## Verification

`tests/FactoryFlow.Data.IntegrationTests` exercises the actual Data and PlcClient
assemblies on .NET Framework 4.8, against a separate SQL database and an owned
simulator on port 4843. It does not interrupt the server on 4840 or write test jobs
into FactoryFlowDB. The test databases are retained for inspection and named
`FactoryFlowDB_LoggingTests_<UTC date/time>`.

Latest run: **24 passed, 0 failed**. It verified additive migration/preservation,
recipe reads, idempotent batch/events, final weight/timestamps, safe terminal status,
coherent snapshots, stops, faults, E-stop alarms, monitoring loss, unsupported recipe
rejection, simulated SQL outage, durable replay after restart, and real encrypted
OPC UA completion/stop/guard-fault records in SQL. The live test also started a new
job directly from Completed, exercising the earlier repeat-job change.

Build/run the test (Windows, .NET Framework 4.8 + .NET 8 runtime required):

```powershell
dotnet build FactoryFlow/tests/FactoryFlow.Data.IntegrationTests
.\FactoryFlow\tests\FactoryFlow.Data.IntegrationTests\bin\Debug\net48\FactoryFlow.Data.IntegrationTests.exe E:\VisualStudioProject\Project2\FactoryFlow E:\VisualStudioProject\Project2\FactoryFlow\.verification\sql\simulator\FactoryFlow.PlcSimulator.exe
```

The second argument may point to any freshly built simulator executable. The test
creates its own database and requires local SQL CREATE DATABASE permission; the
normal HMI only uses FactoryFlowDB (initial migration requires schema permissions).

Evidence: `sql-integration-results.log`, `sql-tests-build.log`, `sql-hmi-build.log`.
The project retains the previously reported Microsoft Extensions net48 compatibility
warnings. No database credentials or generated certificates are committed.
