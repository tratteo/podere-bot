import math
import sqlite3
from datetime import datetime, timedelta, timezone
from pathlib import Path

root = Path(__file__).resolve().parent
db = sqlite3.connect(root / "panel.db")
db.executescript(
    """
    CREATE TABLE IF NOT EXISTS temperature_hourly (
        hour_utc INTEGER PRIMARY KEY,
        avg REAL NOT NULL,
        min REAL NOT NULL,
        max REAL NOT NULL,
        samples INTEGER NOT NULL
    );
    CREATE TABLE IF NOT EXISTS sensor_hourly (
        hour_utc INTEGER NOT NULL,
        sensor_id TEXT NOT NULL,
        label TEXT NOT NULL,
        avg REAL NOT NULL,
        min REAL NOT NULL,
        max REAL NOT NULL,
        samples INTEGER NOT NULL,
        PRIMARY KEY (hour_utc, sensor_id)
    );
    CREATE INDEX IF NOT EXISTS sensor_hourly_id ON sensor_hourly(sensor_id, hour_utc);
    CREATE TABLE IF NOT EXISTS events (
        id INTEGER PRIMARY KEY AUTOINCREMENT,
        topic TEXT NOT NULL,
        title TEXT NOT NULL,
        detail TEXT,
        at_utc INTEGER NOT NULL
    );
    CREATE TABLE IF NOT EXISTS panel_devices (
        id TEXT PRIMARY KEY,
        name TEXT NOT NULL UNIQUE COLLATE NOCASE,
        key_hash TEXT,
        pairing_hash TEXT,
        pairing_expires_at INTEGER,
        created_at INTEGER NOT NULL,
        created_by TEXT,
        last_seen_at INTEGER,
        revoked_at INTEGER
    );
    """
)

now = datetime.now().astimezone()
hour = now.replace(hour=0, minute=0, second=0, microsecond=0)
sensors = [
    ("local", "Locale", 19.0, 1.3, 0.0),
    ("28ff-a1", "Camera Trat", 13.8, 2.2, 0.5),
    ("28ff-b2", "Soggiorno", 17.9, 0.8, 1.2),
    ("28ff-c3", "Esterno", 8.5, 3.2, 2.0),
]

i = 0
while hour <= now:
    ts = int(hour.astimezone(timezone.utc).timestamp())
    wave = math.sin((i / 24.0) * 2 * math.pi)
    avg = round(18.2 + 1.1 * wave, 1)
    db.execute(
        "INSERT OR REPLACE INTO temperature_hourly(hour_utc, avg, min, max, samples) VALUES(?, ?, ?, ?, ?)",
        (ts, avg, round(avg - 0.9, 1), round(avg + 0.9, 1), 12),
    )
    for sid, label, base, amplitude, phase in sensors:
        value = round(base + amplitude * math.sin((i / 24.0) * 2 * math.pi + phase), 1)
        db.execute(
            """
            INSERT OR REPLACE INTO sensor_hourly(hour_utc, sensor_id, label, avg, min, max, samples)
            VALUES(?, ?, ?, ?, ?, ?, ?)
            """,
            (ts, sid, label, value, round(value - 0.4, 1), round(value + 0.4, 1), 12),
        )
    hour += timedelta(hours=1)
    i += 1

db.commit()
db.close()
print(f"demo panel.db: {i} hourly points, {len(sensors)} sensors")
