import asyncio, os, tempfile, time
import edge_tts

async def one(i):
    fd, path = tempfile.mkstemp(prefix="tts-persist-", suffix=".mp3")
    os.close(fd)
    t0 = time.perf_counter()
    c = edge_tts.Communicate("Test hlasu.", "cs-CZ-AntoninNeural", rate="-4%", pitch="-5Hz")
    await c.save(path)
    ms = int((time.perf_counter() - t0) * 1000)
    size = os.path.getsize(path)
    os.remove(path)
    print(f"RUN={i} MS={ms} SIZE={size}", flush=True)

async def main():
    for i in range(1, 4):
        await one(i)

asyncio.run(main())
