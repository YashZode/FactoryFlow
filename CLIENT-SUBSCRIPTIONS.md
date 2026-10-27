# FactoryFlow PLC subscriptions — implementation and verification

This records the earlier subscription milestone. See `README.md` and
`RESUME-READINESS.md` for the later configurable targets, safe fault reset,
operator roles, alarm/history screens, collector and current verification.

## What changed

The repository actually targeted .NET 10 when inspected. HMI, Core, Data and PlcClient
now target the requested .NET Framework 4.8, using C# 12 with the installed .NET 10 SDK.
The simulator and original smoke test remain on .NET 8. OPC Foundation packages are
pinned to 2.0.1. The simulator/state machine and original smoke test were not modified.

Inspected: PlcCommunicationService, PlcNodeIds, FactoryFlowClientTelemetry,
MainWindow.xaml/.cs, all project files/references, the original smoke-test Program,
FactoryFlowNodeManager, simulator Program and PLC state machine.

Modified/added:

- `FactoryFlow.PlcClient/Services/PlcCommunicationService.cs`: session lifecycle,
  subscriptions, initial reads, commands, reconnect and asynchronous cleanup.
- `FactoryFlow.PlcClient/Services/PlcMachineSnapshot.cs`: immutable nullable values
  and safe command eligibility for the HMI.
- `FactoryFlow.PlcClient/Services/PlcValueCache.cs`: typed data-quality and timestamp checks.
- `FactoryFlow.PlcClient/AssemblyInfo.cs`: exposes internals to the dedicated tests only.
- `FactoryFlow.HMI/MainWindow.xaml` and `.xaml.cs`: live dashboard and UI dispatching.
- HMI, Core, Data and PlcClient `.csproj`: net48 compatibility; explicit Configuration
  package in PlcClient; executable binding redirects in HMI.
- `tests/FactoryFlow.PlcClient.IntegrationTests`: net48 integration executable,
  referencing the real communication service, with its own child simulator.
- `FactoryFlow.slnx`: includes the new test project.

PlcNodeIds and FactoryFlowClientTelemetry were reviewed and retained.

## Bugs fixed

The old HMI fabricated Running/Idle using a local Boolean; it did not talk to the
PLC. The old service omitted both interlock subscriptions/initial reads, converted
OPC UA values without strict type/range checking, had no reconnect supervision,
and attempted subscription deletion after session disposal. Duplicate Connect calls,
partial connection failures and application-certificate lifetimes were not managed.

The HMI now reports actual PLC values, never infers Running from a successful Write,
and clears all machine values on loss. A UI verification also caught and fixed a
stale error message that remained after successful reconnection.

## How it works

One secure Session owns one Subscription at 250 ms. Four MonitoredItems monitor
Status, ActualWeight, EStopActive and GuardClosed, each with 250 ms sampling and a
10-value queue. Notification callbacks drain `DequeueValues()`. A
StatusValueTimestamp filter includes idle heartbeat samples from the simulator.
A batched initial Read populates all four values before the connection becomes ready.

Only Good-quality, correctly typed values are usable. Status must be 0–3; weight
must be finite and nonnegative. Missing/stale/future source timestamps invalidate
values. Older out-of-order samples are ignored. A local 5-second freshness watchdog
checks arrivals without issuing PLC reads. Simulator/client clocks must agree within
5 seconds. Invalid values disable both commands. Nullable values explicitly mean
Unknown, not Idle/false/zero.

SDK callbacks are serialized into immutable snapshots, then WPF uses
`Dispatcher.BeginInvoke`. Snapshot revisions prevent a delayed UI callback from
reinstating old data. No WPF polling timer is used. The event list is capped at 200.

A single asynchronous supervisor detects bad KeepAlive and stale notifications,
invalidates the cache, closes the old Session, and retries after 1, 2, 4, then at
most 8 seconds. Each successful reconnect creates new monitored items and rereads
all values. All network operations have bounded timeouts and use cancellation.
The I/O semaphore serializes commands with session replacement. Each operation
captures its original Session before waiting, so a queued command cannot migrate
to a replacement Session. Start/Stop are never replayed.

Disconnect cancels the supervisor and waits for cleanup. Closing the HMI temporarily
cancels Window.Closing, asynchronously disposes the service, then closes the window.
Handlers are detached, the session closes its server subscriptions, and local
subscriptions, sessions and application certificate resources are disposed.

The recipe selector is now loaded from SQL Server. The PLC still targets 500 kg;
only compatible recipes can start. See DATABASE-LOGGING.md for persistence details. Completed permits another Start Batch, resetting weight to zero.
Fault still requires a simulator restart.

## Build and run

Use PowerShell from the repository parent:

```powershell
cd E:\VisualStudioProject\Project2
dotnet restore FactoryFlow/FactoryFlow.slnx
dotnet build FactoryFlow/FactoryFlow.slnx --no-restore
```

Requires Windows, .NET Framework 4.8 runtime/developer targeting pack, .NET 8 runtime,
and an SDK supporting C# 12 and .slnx (the verified machine uses SDK 10.0.402).

Start the simulator in one terminal:

```powershell
dotnet run --project FactoryFlow/FactoryFlow.PlcSimulator --no-build
```

Start the HMI in another terminal:

```powershell
Start-Process .\FactoryFlow\FactoryFlow.HMI\bin\Debug\net48\FactoryFlow.HMI.exe
```

Click **Connect PLC**. The endpoint is `opc.tcp://localhost:4840` with
Basic256Sha256 / SignAndEncrypt / Anonymous. Start is enabled only when all values
are valid, the PLC is Idle or Completed, E-stop is inactive and the guard is closed. Stop is
available only with valid values and Running state.

Do not launch a second simulator on the same port. Check port ownership if startup
fails. This task did not stop an unrelated process or alter the UALDS service.

To close: close the HMI normally (it cancels reconnect and cleans up), then Ctrl+C
in the simulator terminal. Closing the HMI intentionally does not send StopBatch;
connection lifecycle and machine commands are separate actions.

## Certificates

Certificate verification remains enabled. No accept-all callback or insecure endpoint
was introduced. Minimum RSA key size is 2048; SHA-1-signed certificates are rejected.
Each client identity has its own private-key store. Only verified public `.der`
certificates were copied into peer trust folders.

Verified development identities:

| Identity | Subject | SHA-1 identification thumbprint |
|---|---|---|
| HMI | CN=FactoryFlow.HMI | 5A7503DF6C3219441B2891E5A50A1FB83D130DD0 |
| Simulator | CN=FactoryFlow.PlcSimulator, O=FactoryFlow | 005368D80077051ECDDE5DE4F7BF51F69FE77AB2 |
| Integration test | CN=FactoryFlow.ClientIntegrationTests | 40B9F03D6045B7B0DFE28964F8E95F3A3F57CE8B |

A SHA-1 thumbprint is the certificate identifier, not its signature algorithm.
Public files were checked for subject, expiry and absence of a private key.

HMI PKI: `FactoryFlow.HMI/bin/Debug/net48/pki`

Simulator PKI: `FactoryFlow.PlcSimulator/bin/Debug/net8.0/pki`

Each has `own/certs`, `own/private`, `trusted/certs`, `issuers` and `rejected`.
The HMI and server currently trust each other's verified public certificates. No
manual trust action is required for these Debug builds. Other build directories,
regenerated certificates or another machine require a new explicit trust exchange:
verify the public files, copy only the relevant DER from each `own/certs` into the
other `trusted/certs`, then restart both applications. Never copy `own/private`.
Generated PKI is excluded from source control. This remains a local development
simulator using Anonymous access, not production operator authorization.

## Automated verification

```powershell
.\FactoryFlow\tests\FactoryFlow.PlcClient.IntegrationTests\bin\Debug\net48\FactoryFlow.PlcClient.IntegrationTests.exe E:\VisualStudioProject\Project2\FactoryFlow
```

The harness verifies that port 4842 is free, starts only its own simulator process,
and restarts/stops only that child. It does not use or kill the server on 4840.
It explicitly provisions public certificate trust for its separate test identity.
It cleans up the child in `finally`, and is repeatable after certificates already exist.

Final integration run: **44 passed, 0 failed**. Coverage includes strict quality,
invalid status/NaN/stale values, initial state, secure connection, dynamic namespace,
full/short tag names, invalid writes, all four subscriptions, multiple distinct
weights, commands and interlocks, actual command readback, external-client status
updates, loss detection, disabled offline commands, reconnect, restored subscriptions,
no command replay, no callbacks after disconnect, repeated connection/disposal,
initial-failure recovery and cancellation during reconnect backoff.

Logs: `client-integration-results.log`, `solution-build.log`.

Final full solution rebuild: **0 errors, 32 warnings**. Eight are pre-existing Core
model nullable-property warnings. The other 24 are eight transitive Microsoft
Extensions 10.10.0 compatibility warnings repeated across PlcClient/HMI/tests. Those
packages ship net462 assets but say they are untested on net48. Warnings are not
suppressed; the used OPC UA paths were exercised successfully on .NET Framework 4.8.
This runtime verification does not turn that dependency warning into a general
vendor support guarantee.

Live WPF checks: connected/initial values, Running, automatic weight updates,
Completed at 500 kg (the original UI check disabled both buttons; the subsequent repeat-job change enables Start), and Unknown/disabled commands
when the owned simulator stopped. Normal window closing during reconnect terminated
the application. The original smoke-test source remains unchanged.

Final UI regression checks also confirmed automatic reconnect after an initial
connection failure, clearing the previous error on recovery, and Stop returning the
machine to Idle with weight retained (210 kg in the observed run). Both the connected
HMI and the task-owned default-port simulator were closed after verification. The
integration harness cleaned up its port-4842 child. No required test remained blocked.
The original smoke test was built and reviewed, but was not rerun in this task;
the new 44-check harness exercised the actual net48 communication service instead.


