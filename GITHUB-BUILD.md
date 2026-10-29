# Build ProSight on GitHub

1. Extract this source ZIP. Copy the contents of ProSight-ProSightX into your
   repository root, alongside ProSight.sln. Include .github and global.json.
   Do not nest the ProSight-ProSightX folder inside the repository.
2. Commit and push to ProSightX. The push starts the workflow automatically.
3. Open Actions → Build ProSight downloads → the latest run.
4. When the jobs succeed, download the required artifact:

| Artifact | Contents |
| --- | --- |
| ProSight-Windows-x64 | Portable Windows application ZIP |
| ProSight-macOS-Intel | Intel Mac application ZIP |
| ProSight-macOS-Apple-Silicon | Apple Silicon Mac application ZIP |

GitHub wraps each artifact in a ZIP. Extract that outer ZIP to find the versioned
ProSight ZIP. Upload the versioned ZIP to a GitHub Release, or extract it to use
ProSight.exe on Windows / ProSight.app on macOS. Preserve the Mac app by keeping
it inside its versioned ZIP when sharing it.

These are portable packages, not Windows setup installers or Mac DMGs.
The .NET runtime is bundled. GitHub installs build tools on its own runners.
Artifacts are retained for 30 days. The workflow does not publish a release.

Manual builds: Run workflow becomes available when the workflow exists on the
repository's default branch. Select ProSightX in that menu to build that branch.
Push-triggered builds do not require merging into the default branch.

## Checks and signing

Each Mac build runs on its matching processor architecture. The existing script
builds the app, verifies its ad-hoc signature and runs its self-test before ZIP
creation. Windows checks publishing and ZIP creation; its existing --selftest
opens a dialogue box and is deliberately not run on the unattended runner.
Run ProSight.exe --selftest manually after downloading the Windows build.

No Developer ID certificate, Apple notarisation or Windows signing certificate
is configured. macOS may block downloaded ad-hoc builds; see README.md for
signing and distribution information. Test the UI and actual network functions
on the target machines before production use.

If a job fails, open the red step in Actions and read the error. A successful
build is not proof of all live networking behaviour.
