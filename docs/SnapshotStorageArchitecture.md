# Snapshot Storage Architecture

BohemiX keeps writable save profiles as ordinary directories so the existing NTFS Junction routing remains unchanged. Immutable snapshots use a versioned content-addressed store instead of copying each complete profile directory.

New automatic protection is save-node based. Each completed `.whs` file is an independently selectable backup node; complete profile snapshots remain available as an explicit compatibility and disaster-recovery option.

## Layout

```text
saves/snapshots/
  objects/v1/AB/<SHA256>.bxc
  manifests/<profile-id>/<snapshot-id>.json
  manifests/nodes/<profile-id>/<node-id>.json
  <profile-id>/<snapshot-id>/...       # legacy directory snapshots
```

Files are split with FastCDC-compatible Gear hashing at 256 KiB minimum, 1 MiB target, and 4 MiB maximum boundaries. Each raw chunk is addressed by SHA-256. Brotli Fastest is used only when it reduces the chunk by at least ten percent. Objects are immutable and written through staging followed by an atomic move.

Manifests contain safe relative file paths, logical lengths, file hashes, the directory fingerprint, and ordered chunk hashes. The SQLite `SaveSnapshots.StorageFormat` column selects either `LegacyDirectory` or `ChunkedManifest`; existing rows default to the legacy format.

## Save Node Library

- The initial scan stores the ten most recent `.whs` files plus every `crucialdecision*.whs`; all other existing files are recorded as observations without copying their payload.
- Later created or changed files are captured after two stable file-stat samples one second apart. Startup, game exit, profile switching, and Save Manager refresh reconcile missed watcher events.
- Critical-decision and manual (`save*`, `permanent*`, or `Potion`) nodes are important by default. Important nodes are never automatically pruned; ordinary nodes use a configurable per-profile rolling window of 1-200, defaulting to 10.
- Restoring a node materializes and verifies one file before touching the profile. A conflicting current file is first captured as an important restore-safety node, then replaced atomically. Other live saves are never removed.
- Automatic retention and garbage collection affect only the backup library. BohemiX never deletes old `.whs` files from the writable game profile.

`SaveBackupNodes` stores node metadata and importance state. `SaveBackupFileObservations` prevents the first reconciliation from importing the entire historical profile, while still detecting later changes. Node and full-snapshot manifests share the same object store, so garbage collection marks references from both tables before deleting objects.

## Transactions And Recovery

- Capture writes missing objects and the manifest before inserting the snapshot row. Failed operations remove the manifest; unreferenced objects are reclaimed by mark-and-sweep.
- Restore verifies every object and file while materializing into staging, then verifies the directory fingerprint before the existing directory swap and rollback sequence begins.
- Delete moves the manifest or legacy directory to staging, commits the database deletion, and then sweeps objects not referenced by any live manifest.
- Startup reconciliation removes orphan manifests and objects only when every database-referenced manifest is valid. Missing or corrupt live manifests are reported and never silently discarded.
- Package schema v1 remains the external format. Chunked snapshots stream directly into ZIP entries, while imported snapshot histories are captured into the object store.

## Legacy Migration

The singleton save service performs one migration per idle pass, newest first. Foreground save operations cancel the pass and take priority. Migration estimates missing raw chunk bytes first and preserves the larger of 1 GiB or ten percent of the volume as free space. The legacy directory is deleted only after capture, full fingerprint verification, and the SQLite format switch succeed.
