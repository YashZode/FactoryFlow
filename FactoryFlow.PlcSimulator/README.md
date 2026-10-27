# FactoryFlow Step 7.2 — OPC UA simulator

Targets .NET 8 and pins the OPC Foundation Server and Configuration packages to 2.0.1.

From E:\VisualStudioProject\Project2:

```powershell
dotnet run --project FactoryFlow/FactoryFlow.PlcSimulator
```

Connect to `opc.tcp://localhost:4840`. The SDK also advertises the machine hostname
(`opc.tcp://laptop-l89ek2po:4840/` on the verified machine). Use Basic256Sha256,
SignAndEncrypt, Anonymous. No unsecured session endpoint is enabled.
`-- --port 4841` selects an alternate port if needed. The Windows UALDS discovery
service was stopped with approval because it occupied 4840; its startup type was
not changed. If it starts again, stop it from an administrator PowerShell with
`Stop-Service UALDS`, or use the alternate port. After stopping FactoryFlow,
`Start-Service UALDS` restores the discovery service.

## UaExpert

1. Add a server using Custom Discovery: `opc.tcp://localhost:4840`.
2. Select Basic256Sha256 / SignAndEncrypt and Anonymous authentication.
3. Inspect and trust FactoryFlow's server certificate in UaExpert.
4. The first connection may be rejected until the server trusts UaExpert.
   Inspect the UaExpert certificate in `bin/Debug/net8.0/pki/rejected/certs` under
   this project, verify its identity/thumbprint, and copy only that `.der` file to
   `bin/Debug/net8.0/pki/trusted/certs`. Alternatively export UaExpert's public
   application certificate from its certificate manager into that trusted folder.
   Restart FactoryFlow and reconnect. Never copy private keys between applications.
5. Browse Objects → Line1, then drag all six tags into Data Access View.

Namespace URI: `urn:factoryflow:plc:line1`. The verified runtime namespace index is
2; clients should resolve the URI through NamespaceArray instead of hardcoding
an index. String identifiers remain stable across restarts.

| NodeId in verified runtime | Type | Access |
| --- | --- | --- |
| ns=2;s=Line1.Status | Int32 | Read only |
| ns=2;s=Line1.ActualWeight | Double | Read only |
| ns=2;s=Line1.StartRequest | Boolean | Read/write |
| ns=2;s=Line1.StopRequest | Boolean | Read/write |
| ns=2;s=Line1.EStopActive | Boolean | Read/write, simulation only |
| ns=2;s=Line1.GuardClosed | Boolean | Read/write, simulation only |

Status values: 0 Idle, 1 Running, 2 Completed, 3 Fault.

- Initially Status=0, ActualWeight=0, EStopActive=false, GuardClosed=true.
- Write StartRequest=true: Status immediately becomes 1 if interlocks allow it.
  Weight increases by 5 every 250 ms, reaching 500 and Status=2 after about 25 s.
- While running, write StopRequest=true: Status becomes 0; weight is retained.
  Starting again resets weight to 0.
- StartRequest and StopRequest are momentary commands and automatically return
  false. A successful UA write acknowledges delivery; inspect Status to see
  whether the existing PLC state machine accepted StartBatch().
- EStopActive=true or GuardClosed=false blocks startup. During a batch, either
  causes Fault on the next tick (within 250 ms). Interlock node changes publish
  immediately; state transitions retain the existing Tick() behavior.
- After Completed, write StartRequest=true to begin the next batch. The completed
  result remains visible until a permitted start resets weight to 0 and enters Running.
  E-stop must be inactive and the guard closed. Fault still requires a simulator
  restart; StopRequest does not clear Fault or Completed.
- Writes to Status/ActualWeight fail with BadNotWritable. Non-Boolean writes to
  writable tags fail with BadTypeMismatch.
- Values have UTC source timestamps; subscribers receive changes. To observe
  timestamp-only updates while Idle, select a StatusValueTimestamp data-change
  filter in the client. Server timestamps are supplied by the OPC UA stack.

## Thread safety and certificates

All PLC properties and state-machine methods use one reentrant lock. UA writes
and periodic publication also share the node manager lock, and publish a coherent
PLC snapshot under its lock. One awaited PeriodicTimer prevents overlapping ticks.

A self-signed RSA SHA-256 application certificate is created locally on first
startup. Certificate validation stays enabled: AutoAcceptUntrustedCertificates is
false, SHA-1 certificates are rejected, and minimum key size is 2048. PKI stores
are relative to the executable, so Debug/Release/published builds have separate
stores. Do not commit generated certificates/private keys.

This is a development simulator: any trusted application can use Anonymous to
write the simulated commands/interlocks. Production needs deployed certificates,
managed trust, user authorization, protected key storage, and network access
controls. The SDK may bind all interfaces even when localhost is the configured
URL; do not treat that hostname as a loopback firewall restriction.

## Verified integration test

`../tests/FactoryFlow.OpcUaSmokeTest` is a separate .NET 8 console test using the
same 2.0.1 client package. It passed against the encrypted localhost:4840 endpoint:
six reads, read-only/type rejection, blocked starts, start/stop, weight progression,
command reset, guard-triggered Fault, and subscription values/source timestamps.
The test deliberately leaves the PLC in Fault, so restart before repeating it.

To prepare a fresh test certificate:

```powershell
dotnet run --project FactoryFlow/tests/FactoryFlow.OpcUaSmokeTest -- --prepare
```

Explicitly exchange only public `.der` certificates between each application's
`pki/own/certs` and the other application's `pki/trusted/certs`, then restart the
server and run:

```powershell
dotnet run --project FactoryFlow/tests/FactoryFlow.OpcUaSmokeTest
```

The smoke-test certificate was explicitly trusted for this verification. Remove
its public certificate from the server trusted store when no longer needed.
Set `FACTORYFLOW_VERBOSE=1` to enable SDK diagnostic logging.

SDK reference: https://github.com/OPCFoundation/UA-.NETStandard

