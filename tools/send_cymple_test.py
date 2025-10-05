#!/usr/bin/env python3
"""
send_cymple_test.py

Sends test UDP packets to CympleFaceTracking module listening on port 22999.

Packet layout (as expected by the module code):
- 4 bytes: prefix (int32 little-endian) -> should be 0xFFFFFFFD (signed -3)
- 4 bytes: flags (uint32 BIG-ENDIAN/network order) -> bitmask: 0x01 mouth, 0x02 eye
- 2 bytes: type (uint16 little-endian) -> 0
- 2 bytes: length (int16 little-endian) -> number of bytes following (optional)
- N floats (4 bytes each, little-endian) matching Constants.blendShapeNames count (39)

This script sends a packet with both eye and mouth flags set and sample float values.
"""
import socket
import struct
import time

HOST = '127.0.0.1'
PORT = 22999

# Constants matching module
MSG_PREFIX = 0xFFFFFFFD  # int32 (== -3 signed)
FLAG_MOUTH_E = 0x01
FLAG_EYE_E = 0x02

BLENDSHAPE_COUNT = 39

# Build sample blendshape values (39 floats)
# you can tweak these values to test different expressions
values = [0.0] * BLENDSHAPE_COUNT
# Example: small eye pitch/yaw and slight pupil
values[0] = 0.1   # EyePitch
values[1] = 0.2   # EyeYaw_L
values[2] = -0.2  # EyeYaw_R
values[3] = 0.3   # Eye_Pupil_Left
values[4] = 0.3   # Eye_Pupil_Right
values[5] = 0.1   # EyeLidCloseLeft
values[6] = 0.1   # EyeLidCloseRight
# some mouth values
values[13] = 0.2  # JawOpen
values[15] = 0.0  # MouthClose
values[30] = 0.4  # MouthSmileLeft
values[31] = 0.4  # MouthSmileRight

sock = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
print(f"Sending UDP test packets to {HOST}:{PORT} (Ctrl-C to stop)")
try:
    while True:
        # prefix: int32 little-endian
        prefix_bytes = struct.pack('<i', MSG_PREFIX if MSG_PREFIX <= 0x7fffffff else struct.unpack('<i', struct.pack('<I', MSG_PREFIX))[0])
        # flags: uint32 big-endian (network order)
        flags = FLAG_MOUTH_E | FLAG_EYE_E
        flags_bytes = struct.pack('>I', flags)
        # type: uint16 little-endian
        msg_type = 0
        type_bytes = struct.pack('<H', msg_type)
        # length: int16 little-endian, number of bytes of payload (optional)
        payload_len = BLENDSHAPE_COUNT * 4
        length_bytes = struct.pack('<h', payload_len)

        # floats: little-endian
        floats_bytes = b''.join(struct.pack('<f', v) for v in values)

        packet = prefix_bytes + flags_bytes + type_bytes + length_bytes + floats_bytes
        sock.sendto(packet, (HOST, PORT))
        print("packet sent")
        time.sleep(0.5)
except KeyboardInterrupt:
    print("stopped")
finally:
    sock.close()
