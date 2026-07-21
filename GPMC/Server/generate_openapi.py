import json
from main import app

if __name__ == "__main__":
    with open("openapi.json", "w", encoding="utf-8") as f:
        json.dump(app.openapi(), f, indent=2, ensure_ascii=False)
    print("OpenAPI схема сохранена в openapi.json")