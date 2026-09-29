# SSDRescue

Windows x64 GUI utility for conservative recovery of files from an unstable SSD/USB disk.

Design goals:
- Source is never deleted, renamed, trimmed, formatted, or overwritten.
- Source/destination paths are persisted in a SQLite job database.
- Each file has a durable state: Pending, Copying, Success, Failed, VerifyFailed, SourceChanged, Missing.
- Failed files are retried in later rounds; a timeout/disconnect is not treated as permanent corruption.
- Copies use a temporary `.partial` file and checkpoint every 4 MiB.
- A disconnected source pauses the job instead of continuing to hammer the device.
- Completed files are verified with SHA-256 on the destination.
- Logs and per-attempt records are retained.
- A Rescan button invokes Windows DiskPart `rescan`; if the controller/device is genuinely hung, a reboot may still be required.

This is an early conservative build. It intentionally does NOT implement destructive refresh/format/TRIM operations.
