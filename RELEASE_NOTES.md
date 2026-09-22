# FIFINE SC3 RGB+ v2.5.2

Stable lighting hotfix.

- Fixes color, brightness, and effect changes failing after v2.5.1
- Keeps the lighting worker independent from WPF UI controls
- Preserves the event-driven Custom Button shortcut and mixer-freeze fix from v2.5.1
- Preserves saved lighting restoration at Windows startup and after reconnect

The issue was caused by the background lighting worker reading the brightness slider directly. WPF rejected that cross-thread UI access before the first FC/04 lighting command. The worker now uses the saved brightness value, and color, brightness, Static, Breathing, Rainbow, Pulse, and Color Cycle operate through the existing serialized RGB transport.

RGB+ Firmware 1.5 is unchanged in this application hotfix.

## Included from v2.5.1

- Fixes intermittent full-mixer and audio freezes caused by continuous FC/02 polling
- Moves Custom A/B/C/D shortcuts to event-driven MI_03 interrupt reports
- Keeps shortcut enable/disable, sound suppression, tray operation, and one launch per press
- Applies saved lighting automatically at Windows startup without opening the main window
- Restores saved lighting once after a real SC3 reconnect
- Includes the physically validated CBTN v4 RGB+ Firmware 1.5 package

Mixer firmware updates never run silently. Custom Button Shortcuts remain unavailable until a verified RGB+ Firmware 1.5 device with the CBTN v4 event transport is connected. Button events use MI_03 interrupt input and do not continuously poll FC/02.

Production Mod 1.5 package SHA-256:
`02DACD0F2692E4EA064647378718047DED3C197081B53A26A95A0E8BDF6F9ACF`

Production Mod 1.5 package CRC16:
`FB45`
