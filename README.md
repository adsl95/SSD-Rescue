# SSDRescue

Windows x64 GUI utility for conservative recovery of files from an unstable SSD/USB disk.

The rescue program never deletes, renames, formats, trims, or overwrites the source. It stores job state in SQLite, keeps per-attempt records, retries failed files in later rounds, resumes `.partial` destination files, and verifies completed files with SHA-256.

This is intentionally a rescue-only build. Destructive refresh/format/TRIM functions are not included.
