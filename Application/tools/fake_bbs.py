#!/usr/bin/env python3
"""Stand-in for the C64 BBS, for testing the relay without hardware.

Dials the relay's BBS port, waits for CONNECT, then greets the caller and
echoes what they type. ESC or typing BYE hangs up (a C64 has no ESC key).
When the call ends it redials, like the real BBS will.

    python tools/fake_bbs.py relay-host [bbs-port]

Then call the relay's caller port with SyncTERM or `telnet relay-host 23`.
"""
import socket
import sys
import time

host = sys.argv[1] if len(sys.argv) > 1 else "127.0.0.1"
port = int(sys.argv[2]) if len(sys.argv) > 2 else 6400

while True:
    try:
        with socket.create_connection((host, port)) as s:
            print(f"Connected to relay {host}:{port}, waiting for a caller...")
            line = b""
            while not line.endswith(b"\n"):
                chunk = s.recv(1)
                if not chunk:
                    raise ConnectionError("relay closed the connection")
                line += chunk
            print(f"Relay said: {line!r}")
            s.sendall(b"\r\nWelcome to the fake BBS. Type something; ESC or BYE hangs up.\r\n> ")
            typed = b""
            while True:
                data = s.recv(1024)
                if not data:
                    print("Caller hung up.")
                    break
                # PETSCII and ASCII letters differ only in case, so compare without it.
                typed = (typed + data)[-16:]
                if b"\x1b" in data or b"bye" in typed.lower():
                    s.sendall(b"\r\nBye!\r\n")
                    print("Hanging up.")
                    break
                s.sendall(data)
    except OSError as e:
        print(f"Connection problem: {e}")
    time.sleep(1)
