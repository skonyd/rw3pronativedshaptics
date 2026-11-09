# RW3Pro.Lab

Measurement and audit code that the app does not ship: used only by `tests/RW3Pro.ContractTests` and
`tests/RW3Pro.HardwareProbe`, which compile these files by link. Keeping them out of
`src/RW3ProNativeDSHaptics` keeps the installed app smaller and makes clear what runs for players.
