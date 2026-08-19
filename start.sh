#!/bin/bash
set -e

cd /app/python
uvicorn main:app --host 0.0.0.0 --port 2282 &

sleep 10

cd /app/dotnet
exec dotnet Immich.ToGPhoto.App.dll