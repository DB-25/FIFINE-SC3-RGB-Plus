# FIFINE SC3 RGB+ v2.5.1

Stable reliability update for Custom Button Shortcuts and background startup.

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
