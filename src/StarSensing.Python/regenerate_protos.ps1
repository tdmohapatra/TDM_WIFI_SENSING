# Regenerate gRPC stubs and fix package-relative imports.
$Root = $PSScriptRoot
Set-Location $Root

& "$Root\venv\Scripts\python.exe" -m grpc_tools.protoc `
    -I./protos `
    --python_out=./protos `
    --grpc_python_out=./protos `
    ./protos/star_sensing.proto

$grpcFile = Join-Path $Root "protos\star_sensing_pb2_grpc.py"
$content = Get-Content $grpcFile -Raw
$content = $content -replace 'import star_sensing_pb2 as star__sensing__pb2', 'from . import star_sensing_pb2 as star__sensing__pb2'
Set-Content $grpcFile $content -NoNewline

Write-Host "Proto stubs regenerated." -ForegroundColor Green
