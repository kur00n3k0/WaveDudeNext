# Linux port — notes for Claude

Started 2026-11-06 on Artix (XFCE on X11, RTX 5060, 16 cores, 15 GB). CLAUDE.md holds the project and its rules, which
apply here unchanged (git on the user's word, no design changes without options, verification matching the change,
dependencies); this file holds only what is Linux's. Keep it short, like CLAUDE.md.

## Decisions (the user's, 2026-11-06, each chosen among options)

- **Shape**: a second front end in **Avalonia** beside the WPF app, which stays; in this repository.
- **Shared layer**: the UI-neutral logic now in `MainWindow` code-behind (live editing and re-runs, history, clipboard,
  export orchestration, settings, the presenter) moves into a new project both front ends use. It changes the WPF app's
  internals, so it goes in reviewable steps whose proof, the Windows UI harnesses, the repository's author runs.
- **Targets**: X11, Arch-based distributions. Packaging: an AUR PKGBUILD, a self-contained tarball, an AppImage; the app
  registers its `.desktop` entry and the MIME types of `.wdproj` and `.wdscheme` in `~/.local/share` at start-up.
- **UI checks**: Avalonia.Headless. **Icons**: Fluent UI System Icons (Microsoft, MIT) as a bundled font. **Text**:
  Selawik, bundled (Segoe UI's metrics). **Manual**: MANUAL.md as HTML made at build time, opened with `xdg-open`.
  **Taskbar progress**: none.
- **Frame spill**: `/var/tmp` (over `$XDG_CACHE_HOME`; `/tmp` is often a tmpfs, memory).
- **File names**: the OS's own forbidden set, so Linux keeps `:` (a probe "A/B: focus" gives `ball_A_B: focus.csv`).
- **MP4**: the system's ffmpeg as a separate process, found on the PATH only (no setting); hardware encoders first,
  then libx264, then OpenH264; 4:2:0 like Windows.

## Setup

- .NET SDK 10.0.403 in `~/.dotnet` by Microsoft's `dotnet-install.sh --channel 10.0` (the user's choice, no root); not
  on the PATH, so every command sets `DOTNET_ROOT=$HOME/.dotnet PATH=$HOME/.dotnet:$PATH`. OpenCL: `opencl-nvidia`
  (pacman), the ICD in `/etc/OpenCL/vendors/nvidia.icd`. ffmpeg 9.0.2 and ffprobe from the system.
- `dotnet build WaveDudeNext.sln -p:EnableWindowsTargeting=true` builds everything with 0 warnings; the WPF app
  compiles here and does not run. `dotnet run --project _tools/library_check -c Release`: 15 s, 663 checks.
- Commands writing outside the repository (`~/.dotnet`, `/var/tmp`, `/tmp`) need `dangerouslyDisableSandbox`.
  Scratch files go to the session's scratchpad, never the repository (CLAUDE.md's `%TEMP%` rule).

## Milestone 1: the library on Linux (2026-11-06)

- `library_check` targets `net10.0` (it had targeted `net10.0-windows` with WPF and used none); the WIC and Media
  Foundation benches are methods marked Windows-only, since CA1416 ignores a guard around a lambda and an attribute on
  one. Its job bench had run "mp4, writer not throttling" unguarded; fixed.
- **Memory** (`FrameMemory`): `MemAvailable` from `/proc/meminfo`; under strict overcommit (`vm.overcommit_memory` 2)
  no more than `CommitLimit − Committed_AS`, which in the default heuristic mode bounds nothing.
- **Seek penalty** (`StorageDevice.Linux.cs`): the mount from `/proc/self/mountinfo` (longest mount point over the
  path's nearest existing folder, links resolved), its device under `/sys/dev/block` (or by the mount's source, btrfs),
  then `queue/rotational`: a partition asks its disk, a stacked device (LVM, dm-crypt, RAID) every device beneath it, a
  hard disk among them winning, as on Windows. Here `/home` (LVM on two hard disks) yes, `/` and `/var/tmp` (NVMe) no,
  `/proc` unknown; a relative path is null. `MountPoint` names the mount for the log and the free-space check.
- **Spill** (`FrameCache`, `FrameStore`): `/var/tmp/WaveDudeNext-<user>`, made 0700; a link or a folder open to group or
  others is refused with a `RunSpecException`, since `/var/tmp` is shared. The file is unlinked once open, so a crash
  leaves nothing; `Sweep` deleting another instance's file is harmless on Linux, the handle keeps the data. The checks
  measure the open file through `/proc/self/fd` on the store's own descriptor (a link's `FileInfo.Length` is its text's).
- **OpenCL** (`OpenClApi`): a static constructor's import resolver loads `libOpenCL.so.1`, the runtime's own probing
  finding only the unversioned development link `libOpenCL.so`.
- **MP4** (`FfmpegMp4Writer`): NV12 by `YuvPlanes` (BT.601, studio range, the Media Foundation path's planes), odd sizes
  padded by the last row and column, piped raw; the encoder is the first candidate that encodes a two-frame test picture
  at the size, remembered per size; quality q as quantiser 51 − 0.4 q (23 at 70, x264's default), no B-frames, a GOP
  per second, the stream tagged `smpte170m`, `tv`; ffmpeg's last error lines in the exception. NVENC refuses the
  checks' 70 × 46, so they exercise libx264; full HD takes NVENC.
- **EXR check**: ffmpeg 9 converts float grey to 16-bit grey as limited range (0.05 → 0, 0.5 → 33516), so the check
  decodes to `grayf32le`; the files themselves were always exact.

## Numbers (this machine, full HD)

GPU step 0.25 ms (9-point) and 0.37 (4th order), with pipelined captures every 2 steps 0.59 and 0.58; OpenCL build
0.1–0.3 s; CPU step 2.7 ms. Export: MP4 by NVENC 4.7 ms a frame, in a job 10.7; PNG encode 28 ms, JPEG 6.5.

## Next

2. Extract the shared layer (steps for the author to prove on Windows). 3. The Avalonia shell: the in-house docking,
the dnSpy look, the four string tables, UI zoom. 4. Tree, forms, preview and overlays, timeline, probe graphs.
5. Export, dialogs, clipboard and drag and drop on X11, file associations. 6. The headless harness, packaging.

## Open

- Milestone 1 is unproven on Windows: `library_check` there, plus `-- bench-export 1920 1080` for the moved WIC and
  Media Foundation benches.
- To be asked at their milestone: one source for the string tables of both front ends; UI zoom on Avalonia's scaling;
  file dialogs through the desktop portal or Avalonia's own.
