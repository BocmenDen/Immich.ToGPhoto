import mimetypes
mimetypes.add_type('image/x-adobe-dng', '.dng')
mimetypes.add_type('image/heic', '.heic')
from gpmc import Client
from pydantic import BaseModel
from fastapi import FastAPI, File, UploadFile, HTTPException, Header, Security
from fastapi.security import APIKeyHeader
import tempfile
from pathlib import Path
import uvicorn

app = FastAPI(title="Google Photos Upload/Delete API")

auth_scheme = APIKeyHeader(
    name="auth_data",
    auto_error=True,
)

class DbPathResponse(BaseModel):
    path: str

@app.get("/db_path", response_model=DbPathResponse, operation_id="GetDBPath", tags=["GPMC"])
async def get_db_path(auth_data: str = Security(auth_scheme)):
    client = Client(auth_data=auth_data)
    if client.db_path is None:
        raise HTTPException(status_code=530, detail="База данных не найдена")
    return {"path": str(client.db_path)}

class DbLoadCacaheResponse(BaseModel):
    status: bool

@app.post("/update_cache", response_model=DbLoadCacaheResponse, operation_id="UpdateCache", tags=["GPMC"])
async def update_cache(auth_data: str = Security(auth_scheme)):
    try:
        client = Client(auth_data=auth_data)
        client.update_cache()
    except Exception as e:
        raise HTTPException(status_code=530, detail=str(e))
    return {"status": str(True)}

class UploadRequest(BaseModel):
    path: str
    threads: int = 3

class UploadResponse(BaseModel):
    files: dict[str, str]

@app.post("/upload_files", response_model=UploadResponse, operation_id="UploadFilesReturnMediaKeys", tags=["GPMC"])
async def upload_files(request: UploadRequest, auth_data: str = Security(auth_scheme)):
    try:
        client = Client(auth_data=auth_data)
        uploaded_files = client.upload(request.path, recursive=True, show_progress=True, delete_from_host=True, threads=request.threads)
        return UploadResponse(files=uploaded_files)
    except Exception as e:
        raise HTTPException(status_code=530, detail=str(e))


class DeleteRequest(BaseModel):
    dedupKeys: list[str]

class DeleteResponse(BaseModel):
    status: bool

@app.post("/delete_files", response_model=DeleteResponse, operation_id="DeleteFiles", tags=["GPMC"])
async def delete_files(request: DeleteRequest, auth_data: str = Security(auth_scheme)):
    try:
        client = Client(auth_data=auth_data)
        items = request.dedupKeys
        chunk_size = 200
        while items:
            chunk = items[:chunk_size]
            client.api.move_remote_media_to_trash(chunk)
            client.api.delete_remote_media_permanently(chunk)
            items = items[chunk_size:]
        return DeleteResponse(status=True)
    except Exception as e:
        raise HTTPException(status_code=530, detail=str(e))

@app.get("/health", operation_id="Health", tags=["Health"])
async def healthcheck():
    return {"status": "ok"}

if __name__ == "__main__":
    uvicorn.run(app, host="0.0.0.0", port=2282)