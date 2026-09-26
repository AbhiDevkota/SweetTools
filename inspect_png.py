import re
import base64

with open('src/LuaToolsGui/Services/HttpServerService.cs', 'r') as f:
    text = f.read()

m = re.search(r'SweetToolsIconPngBase64\s*=\s*\n?\s*"([^"]+)"', text)
if m:
    b64 = m.group(1).strip()
    data = base64.b64decode(b64)
    print("Total length:", len(data))
    print("Bytes from 2600 to end:")
    for i in range(2600, len(data)):
        print(f"[{i}] {data[i]:02x} ({chr(data[i]) if 32 <= data[i] <= 126 else '?'})")



    print("Header:", list(data[:8]))
    # Check chunks in PNG
    idx = 8
    chunks = []
    while idx < len(data):
        length = int.from_bytes(data[idx:idx+4], 'big')
        ctype = data[idx+4:idx+8].decode('latin1', errors='replace')
        chunks.append((ctype, length))
        idx += 12 + length
    print("PNG chunks:", chunks)
    print("Last 32 bytes:", data[-32:])
    print("Does IEND exist in data?", b'IEND' in data)

