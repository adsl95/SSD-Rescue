# SSDRescue

Windows x64 GUI utility for conservative recovery of files from an unstable SSD/USB disk.

## Safety design

- The source is never deleted, renamed, moved, overwritten, formatted, TRIMmed, or otherwise intentionally written by SSDRescue.
- A rescue job is persistent in SQLite under `%LOCALAPPDATA%\\SSDRescue\\jobs.db`.
- Once a job has a file list, its source and destination paths are fixed.
- The source volume serial number is stored and checked. If a drive letter later points to a different volume, the job stops instead of copying from the wrong disk.
- Destination data is first written to `*.partial` and flushed before its checkpoint is committed.
- On restart, any uncheckpointed tail of a `.partial` file is discarded before resuming.
- A completed file is accepted only after size checks, source stability checks, and SHA-256 comparison between source and destination.
- SHA-256 mismatch discards the `.partial` file so the next attempt rereads the source rather than trusting corrupted partial data.
- Failed files are retried in later rounds; a failed attempt is not treated as permanent corruption.
- Attempt history is retained in SQLite.
- If the source volume disappears, active I/O is stopped and the job remains resumable.
- No TRIM/Optimize-Volume or destructive SSD functions are included.

This is intentionally a rescue-only build. Destructive refresh/format/TRIM functions are not included.
