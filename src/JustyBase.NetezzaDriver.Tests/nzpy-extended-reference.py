import asyncio
import datetime
import json
import os
import sys
from decimal import Decimal

import nzpy_extended


def normalize(value):
    if value is None:
        return None
    if isinstance(value, bool):
        return "true" if value else "false"
    if isinstance(value, datetime.datetime):
        return value.isoformat(sep=" ")
    if isinstance(value, datetime.date):
        return value.isoformat()
    if isinstance(value, datetime.time):
        return value.isoformat()
    if isinstance(value, Decimal):
        return str(value)
    if isinstance(value, (int, float)):
        return str(value)
    if isinstance(value, str):
        return value
    raise TypeError(f"Unsupported nzpy-extended result type: {type(value).__name__}")


async def main():
    request = json.load(sys.stdin)
    connection = await nzpy_extended.connect(
        user=os.getenv("NZ_DEV_USER", "admin"),
        password=os.getenv("NZ_DEV_PASSWORD", "password"),
        host=os.getenv("NZ_DEV_HOST", "192.168.0.144"),
        port=int(os.getenv("NZ_DEV_PORT", "5480")),
        database=os.getenv("NZ_DEV_DB", "JUST_DATA"),
    )

    result_sets = []
    try:
        for query in request["queries"]:
            cursor = connection.cursor()
            await cursor.execute(query)
            columns = [column[0] for column in cursor.description]
            rows = await cursor.fetchall()
            result_sets.append({
                "columns": columns,
                "rows": [[normalize(value) for value in row] for row in rows],
            })
    finally:
        await connection.close()

    json.dump({"resultSets": result_sets}, sys.stdout, ensure_ascii=False)


asyncio.run(main())
