# HiBy FC4 â€“ HID control protocol (v1.0)

Worked out from HiByUSBMaster.exe 1.1.0 (Ghidra) and confirmed against the device with read requests only.
Device: VID 0x32BB, PID 0x0004, firmware file name `HiBy_FC4II_v1.1`.

## Transport

- HID interface 2, usage page 0xFF32, usage 0x0003.
- Requests and replies are **feature report ID 5**, 62 bytes including the report ID.
- Request: `05 | command (int32 LE) | payload | zero padding`. Payload is at most 57 bytes.
- Reply: read feature report 5 after sending. Protocol 1.0 has no checksum
  (2.0 adds a 16-bit checksum in the last two bytes; this device does not use it).

## Commands

| Command | Meaning | Payload |
|---|---|---|
| 9 | get parameter | 1 byte: parameter number |
| 7 | set parameter | type byte + data, see "Set payloads". Reply is the ASCII text `success` |
| 6 | get protocol version | device does not answer (so the app assumes 1.0) |

## Parameters (get, command 9)

All numbers are int32 little-endian. First reply byte is the data type.

| Number | Type byte | Content |
|---|---|---|
| 0x00â€“0x09 | 02 | PEQ band: frequency Hz, enabled, filter type, band index, Q x100, gain dB x100 |
| 0x0A | 01 | preamp, dB x100, range -900..900 (-9.00..+9.00 dB) |
| 0x0B | 03 | PEQ on/off, 1 byte |
| 0x0Câ€“0xFF | 04 | not available (reply is always `04 0a`) |

Field meanings inside a band record (enabled / filter type / Q) are read from the layout and
example values, not confirmed by changing them.


## Set payloads (command 7, all confirmed on the device)

| What | Payload after the command |
|---|---|
| preamp | `01` + value (dB x100) |
| PEQ band | `02` + index, on, filter, frequency, Q x100, gain x100 |
| PEQ on/off | `03` + one byte (0 or 1) |

The band field order differs between set (index first) and the get reply (frequency first).
Limits the HiBy app applies before sending a band: index 0-9, on 0-1, filter 0-2,
frequency 20-20000, Q 20-2000, gain -900..900.

## What is not there

No parameter for the hardware volume, gain or digital filter. The device also ignores the
USB Audio hardware volume that Windows sends (it stores the value and does nothing with it).

