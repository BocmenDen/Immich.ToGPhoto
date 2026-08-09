#!/bin/bash
set -e

cd /app/python
uvicorn main:app --host 0.0.0.0 --port 2282 &


echo "Waiting for Uvicorn to start..."
until (echo > /dev/tcp/localhost/2282) &>/dev/null; do
    sleep 1
done
echo "Uvicorn is ready!"

cd /app/dotnet
exec dotnet Immich.ToGPhoto.App.dll