# Optional media runtime

FFmpeg is an upstream dependency for local audio/video merging, not first-party
source. Executables and DLLs are intentionally excluded from this repository.
The desktop application and ordinary downloads can be built without them; media
merging requires a compatible runtime before use.

The previously packaged runtime is identified in [THIRD-PARTY-NOTICES.txt](THIRD-PARTY-NOTICES.txt),
including its archive SHA-256 and upstream source/build references. Obtain it from
the official build provider linked there. Verify the archive hash before extracting
its `bin` runtime files to `media-tools/bin`. An archive with a different hash is a
different dependency version and must be reviewed and documented before release.
Do not replace the documented LGPL shared build with a GPL or non-free build
without reviewing the resulting distribution and licensing obligations.

Keep the LGPL license and notices alongside distributed files, preserve access to
the corresponding upstream source/build instructions, and allow replacement of
the shared libraries as required by their license. The project's MIT license does
not relicense FFmpeg or other third-party components.
