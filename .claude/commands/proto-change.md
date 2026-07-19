---
description: Workflow for adding/changing a gRPC RPC or message in star_sensing.proto
---

The user wants to change the gRPC contract: $ARGUMENTS

`star_sensing.proto` (in `src/StarSensing.Core/Protos/`) is the **single source of truth**.
Never hand-edit generated stubs (`*_pb2.py`, `*_pb2_grpc.py`, or C# `Protos` build output).

Follow this exact order — skipping steps leaves stale stubs that compile but mismatch at runtime:

1. Edit `src/StarSensing.Core/Protos/star_sensing.proto` only.
2. `dotnet build src/StarSensing.Core` — regenerates C# stubs (consumed by Engine + Dashboard via project reference).
3. Wire the change into the implementation:
   - New/changed RPC on `SensingService` → `src/StarSensing.Engine/Services/GrpcSensingService.cs`
   - New/changed RPC on `SignalProcessorService` → `src/StarSensing.Python/server.py`
   - New field consumed by Dashboard → relevant ViewModel/Service (check `PROJECT_GUIDE.md` § gRPC API)
4. Regenerate Python stubs (only needed if `SignalProcessorService` or any message Python touches changed):
   ```powershell
   cd src/StarSensing.Python
   .\venv\Scripts\python.exe -m grpc_tools.protoc -I../StarSensing.Core/Protos --python_out=protos --grpc_python_out=protos ../StarSensing.Core/Protos/star_sensing.proto
   ```
5. Rebuild Engine + Dashboard: `dotnet build src/StarSensing.Engine && dotnet build src/StarSensing.Dashboard`
6. Restart the full stack — partial restarts leave stale schemas talking past each other.

Update `PROJECT_GUIDE.md` § 6 (gRPC API) if you added/removed an RPC.
