# Runbook: submit the PtkMcpServer.dll Defender false positive

**Status:** Ready for use on the affected Windows machine. The owner requested
this corrected runbook on 2026-07-20. Uploading a file to Microsoft and posting
to GitHub remain outward-facing actions: confirm those actions with the
operator before performing them.

**Audience:** An agent or human working on the Windows machine where Microsoft
Defender Antivirus detected the DLL.

**Goal:** Submit the exact detected `PtkMcpServer.dll` to Microsoft for a final
false-positive determination, then prove the correction with current security
intelligence before removing any incident-specific exclusion.

**Tracking:** GitHub issue #7:
<https://github.com/AlsoBeltrix/PowerShell-Token-Killer/issues/7>

**Microsoft references:**

- Developer dispute process:
  <https://learn.microsoft.com/en-us/defender-xdr/developer-faq>
- `MpCmdRun.exe`, including custom scans and `-DisableRemediation`:
  <https://learn.microsoft.com/en-us/defender-endpoint/command-line-arguments-microsoft-defender-antivirus>
- File-submission portal:
  <https://www.microsoft.com/en-us/wdsi/filesubmission>

## Known report

The original report was on Windows Server 2022 Standard (10.0.20348), from a
`net10.0` `win-x64` build at commit `b03a359`. Defender reported
`Trojan:MSIL/AsyncRAT.AB!MTB` for the same DLL in the build output and the
installed `~/.ptk/bin` payload, then quarantined it.

Do not infer or claim an official meaning for the `!MTB` suffix. The relevant
facts are the exact detection name, file hash, security-intelligence version,
scan output, and Microsoft's final determination.

PTK is an open-source MCP server whose documented purpose includes executing
operator-supplied PowerShell/native commands and managing child process trees.
That legitimate behavior is useful context for Microsoft, but it is not proof
by itself. Microsoft must analyze the exact detected file.

## Safety and scope

- Run the Defender commands from an **elevated PowerShell 7** console.
  Microsoft documents elevation as a prerequisite for `MpCmdRun.exe`.
- Do not disable real-time protection.
- Do not add or widen an exclusion while following this runbook.
- Do not mutate the active repository checkout. In particular, do not run
  `git checkout b03a359` in an existing worktree.
- Do not copy the DLL to a non-excluded location before the protected scan;
  real-time protection could quarantine that copy before evidence is captured.
- Scan the intact artifact in place with a custom scan and
  `-DisableRemediation`. Microsoft documents that this combination ignores
  exclusions, applies no remediation, writes no detection event/UI entry, and
  reports the finding in command output.
- Do not assume an exclusion exists. If no intact copy of the exact detected
  DLL remains, stop and ask the operator whether to restore the quarantined
  file or provide an already-preserved copy. Do not change Defender settings to
  manufacture one.
- Never commit the DLL, an evidence bundle, user paths, or submission-account
  information to this repository.

## Step 1: select the intact artifact without changing the checkout

Set the repository path, list likely copies, and make the artifact choice
explicit. Replace `<repo>` and `<chosen-path>`; do not paste the angle-bracket
placeholders literally.

```powershell
$repo = (Resolve-Path -LiteralPath '<repo>').ProviderPath
$reportedCommit = git -C $repo rev-parse HEAD
if ($LASTEXITCODE -ne 0) { throw 'Unable to identify the repository commit.' }

$artifactCandidates = @(
    (Join-Path $repo 'server\PtkMcpServer\obj\Release\net10.0\win-x64\PtkMcpServer.dll')
    (Join-Path $repo 'server\PtkMcpServer\bin\Release\net10.0\win-x64\publish\PtkMcpServer.dll')
    (Join-Path $env:USERPROFILE '.ptk\bin\PtkMcpServer.dll')
)

$artifactCandidates |
    Where-Object { Test-Path -LiteralPath $_ -PathType Leaf } |
    ForEach-Object { Get-Item -LiteralPath $_ } |
    Select-Object FullName, Length, LastWriteTime

$artifact = (Resolve-Path -LiteralPath '<chosen-path>').ProviderPath
Get-Item -LiteralPath $artifact | Select-Object FullName, Length, LastWriteTime
```

Prefer a copy that was actually detected and subsequently preserved under an
operator-created exclusion. The two paths in issue #7 were reported as the
same DLL, but verify rather than assume if both survive:

```powershell
$artifactCandidates |
    Where-Object { Test-Path -LiteralPath $_ -PathType Leaf } |
    ForEach-Object { Get-FileHash -LiteralPath $_ -Algorithm SHA256 }
```

If the surviving copies have different hashes, use the copy tied to the
Defender detection record. Do not submit a newly rebuilt, differently hashed
file merely because it came from the same source commit.

## Step 2: locate current Defender tooling and create local evidence files

Use the newest installed Defender platform copy of `MpCmdRun.exe`; fall back to
the legacy Program Files location only when no platform copy exists.

```powershell
$isAdmin = [Security.Principal.WindowsPrincipal]::new(
    [Security.Principal.WindowsIdentity]::GetCurrent()
).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin) { throw 'Re-open PowerShell 7 as Administrator.' }

$platformPattern = Join-Path $env:ProgramData 'Microsoft\Windows Defender\Platform\*\MpCmdRun.exe'
$mpCmdRun = Get-ChildItem -Path $platformPattern -File -ErrorAction SilentlyContinue |
    Sort-Object LastWriteTime -Descending |
    Select-Object -First 1 -ExpandProperty FullName
if (-not $mpCmdRun) {
    $fallback = Join-Path $env:ProgramFiles 'Windows Defender\MpCmdRun.exe'
    if (Test-Path -LiteralPath $fallback -PathType Leaf) { $mpCmdRun = $fallback }
}
if (-not $mpCmdRun) { throw 'MpCmdRun.exe was not found.' }

$caseDir = Join-Path $env:TEMP (
    'ptk-defender-fp-{0}-{1}' -f (Get-Date -Format 'yyyyMMdd-HHmmss'),
    ([guid]::NewGuid().ToString('N').Substring(0, 8))
)
New-Item -ItemType Directory -Path $caseDir | Out-Null

$hash = Get-FileHash -LiteralPath $artifact -Algorithm SHA256
$hash | Format-List | Out-File (Join-Path $caseDir 'hash.txt')
Get-MpComputerStatus |
    Select-Object AMProductVersion, AMServiceVersion, AntivirusSignatureVersion,
        AntivirusSignatureLastUpdated, RealTimeProtectionEnabled |
    Format-List |
    Out-File (Join-Path $caseDir 'defender-status-before.txt')

# Historical records are useful corroboration. They are not the result of the
# protected scan in Step 3, because -DisableRemediation deliberately creates no
# event/UI detection record.
Get-MpThreat -ErrorAction SilentlyContinue |
    Where-Object ThreatName -Like '*AsyncRAT*' |
    Format-List * |
    Out-File (Join-Path $caseDir 'historical-threats.txt')
Get-MpThreatDetection -ErrorAction SilentlyContinue |
    Sort-Object InitialDetectionTime -Descending |
    Select-Object -First 10 |
    Format-List * |
    Out-File (Join-Path $caseDir 'historical-detections.txt')

Write-Host "Artifact: $artifact"
Write-Host "SHA256:  $($hash.Hash)"
Write-Host "Evidence: $caseDir"
Write-Host "Source checkout HEAD recorded as: $reportedCommit"
Write-Host "MpCmdRun: $mpCmdRun"
```

These evidence files remain local. Review them for unrelated host information
before sharing any content.

## Step 3: update security intelligence and confirm the live detection safely

First request a normal security-intelligence update and record its output. If
the update fails, do not claim the definitions are current. On a managed or
offline server, ask the operator whether its configured update source is
expected to be temporarily unavailable before continuing.

```powershell
& $mpCmdRun -SignatureUpdate 2>&1 |
    Tee-Object -FilePath (Join-Path $caseDir 'signature-update.txt')
$signatureUpdateExit = $LASTEXITCODE

Get-MpComputerStatus |
    Select-Object AMProductVersion, AMServiceVersion, AntivirusSignatureVersion,
        AntivirusSignatureLastUpdated, RealTimeProtectionEnabled |
    Format-List |
    Out-File (Join-Path $caseDir 'defender-status-at-scan.txt')

if ($signatureUpdateExit -ne 0) {
    throw "Defender signature update failed with exit code $signatureUpdateExit; review the recorded output."
}
```

Now scan the original intact file in place. Do not omit
`-DisableRemediation`.

```powershell
& $mpCmdRun -Scan -ScanType 3 -File $artifact -DisableRemediation 2>&1 |
    Tee-Object -FilePath (Join-Path $caseDir 'protected-scan.txt')
$scanExit = $LASTEXITCODE
Write-Host "MpCmdRun exit code: $scanExit"
```

Interpret the command output, not the exit code alone: Microsoft documents
exit code 2 for both an unremediated detection and scan errors.

- If the output names `Trojan:MSIL/AsyncRAT.AB!MTB`, proceed to Step 4.
- If the output clearly reports no threats, stop: current definitions no
  longer reproduce the issue for this hash. Continue with Step 5's validation
  process, but do not create a stale false-positive submission.
- If the output is ambiguous or reports a scan error, stop and diagnose that
  error. Do not submit a file without a confirmed current detection.

Confirm that the artifact still exists and its hash did not change:

```powershell
if (-not (Test-Path -LiteralPath $artifact -PathType Leaf)) {
    throw 'The protected scan did not preserve the artifact; stop and report this unexpected result.'
}
$hashAfterScan = Get-FileHash -LiteralPath $artifact -Algorithm SHA256
if ($hashAfterScan.Hash -ne $hash.Hash) {
    throw 'The artifact hash changed during evidence capture; stop.'
}
```

## Step 4: submit the exact file to Microsoft

This step requires a browser and a Microsoft account. Confirm with the
operator before uploading.

Open <https://www.microsoft.com/en-us/wdsi/filesubmission> and choose:

1. Submitter type: **Software developer**.
2. Product: **Windows Server Antimalware** for the reported Windows Server
   detection, or the closest Defender Antivirus product actually shown by the
   affected host.
3. Priority: use the normal/medium analyst-review priority if offered. Microsoft
   reserves high priority for active-malware emergencies.
4. File: upload the exact `$artifact` directly from its preserved location.
5. Belief: **Incorrectly detected as malware/malicious**.
6. Detection name: `Trojan:MSIL/AsyncRAT.AB!MTB`.
7. Definition version: copy `AntivirusSignatureVersion` from
   `defender-status-at-scan.txt`.

The portal currently accepts a maximum 50 MB file and asks for only the
specific file requiring analysis. If direct DLL upload is impossible, create
an encrypted ZIP or RAR containing only `PtkMcpServer.dll`, using password
`infected` as directed by the portal. Do not use PowerShell's
`Compress-Archive` for that fallback because it cannot create an encrypted
archive.

Suggested additional information (replace the placeholders):

> This is an incorrect detection of an open-source developer tool.
> PtkMcpServer is the server component of PowerShell-Token-Killer
> (https://github.com/AlsoBeltrix/PowerShell-Token-Killer), an MCP server whose
> documented purpose is to execute operator-supplied PowerShell/native commands
> and manage their child process trees for local AI coding harnesses. The
> submitted unsigned .NET 10 win-x64 DLL is built from public source associated
> with commit b03a359. Defender quarantines it during build and after installation,
> preventing the local MCP server from launching; reproduction details are in
> https://github.com/AlsoBeltrix/PowerShell-Token-Killer/issues/7.
> SHA256: <SHA256>. Defender signature version: <AntivirusSignatureVersion>.
> A custom scan with current definitions and remediation disabled reproduced
> Trojan:MSIL/AsyncRAT.AB!MTB on Windows Server 2022 (10.0.20348).

Save the following locally in `submission.txt` inside `$caseDir`:

- submission ID and tracking URL;
- submission date/time in UTC;
- SHA-256 uploaded;
- detection name and signature version;
- the final text submitted.

Wait for a **final determination**. Do not promise or assume a response time.
If Microsoft's final determination is unsatisfactory, use the developer
contact form provided with the submission result and reference the submission
ID; this is Microsoft's documented escalation path.

## Step 5: prove the correction before changing exclusions

After Microsoft reports a clean/incorrect-detection determination, update
security intelligence again and repeat the protected scan against the same
source artifact:

```powershell
& $mpCmdRun -SignatureUpdate 2>&1 |
    Tee-Object -FilePath (Join-Path $caseDir 'post-verdict-signature-update.txt')
if ($LASTEXITCODE -ne 0) { throw 'Post-verdict signature update failed.' }

Get-MpComputerStatus |
    Select-Object AMProductVersion, AMServiceVersion, AntivirusSignatureVersion,
        AntivirusSignatureLastUpdated, RealTimeProtectionEnabled |
    Format-List |
    Out-File (Join-Path $caseDir 'defender-status-post-verdict.txt')

& $mpCmdRun -Scan -ScanType 3 -File $artifact -DisableRemediation 2>&1 |
    Tee-Object -FilePath (Join-Path $caseDir 'post-verdict-protected-scan.txt')
$postVerdictScanExit = $LASTEXITCODE
Write-Host "Post-verdict MpCmdRun exit code: $postVerdictScanExit"
```

If the detection remains, keep any incident-specific exclusion in place,
contact Microsoft through the submission result, and stop. A portal verdict
without a clean current-definition scan is not proof that the operational
problem is fixed.

If the protected scan is clean, perform one real-time/remediation check using
a disposable copy. Have the operator identify an existing parent directory
that is not covered by any Defender exclusion; do not assume `%TEMP%` is
suitable. Create a unique child beneath it so cleanup cannot remove unrelated
content.

```powershell
$verificationParent = (Resolve-Path -LiteralPath '<operator-confirmed non-excluded parent>').ProviderPath
$verificationDir = Join-Path $verificationParent (
    'ptk-defender-verify-{0}' -f ([guid]::NewGuid().ToString('N'))
)
New-Item -ItemType Directory -Path $verificationDir | Out-Null
$verificationCopy = Join-Path $verificationDir 'PtkMcpServer.dll'
Copy-Item -LiteralPath $artifact -Destination $verificationCopy

if (-not (Test-Path -LiteralPath $verificationCopy -PathType Leaf)) {
    throw 'Real-time protection removed the verification copy; the issue is not fixed.'
}

& $mpCmdRun -Scan -ScanType 3 -File $verificationCopy 2>&1 |
    Tee-Object -FilePath (Join-Path $caseDir 'post-verdict-remediating-scan.txt')
$remediatingScanExit = $LASTEXITCODE
Write-Host "Remediating scan exit code: $remediatingScanExit"
```

Require explicit clean scan output and confirm the disposable copy still
exists. Then remove only the exact exclusions that the operator confirms were
added for this incident. Do not remove an exclusion whose provenance or scope
is uncertain.

With normal protection restored and no incident exclusion covering the build
or installed payload:

1. Rebuild and run `scripts/dev-install.ps1` normally.
2. Confirm the installed DLL remains present.
3. From the repository, smoke-test the installed executable:

   ```powershell
   $installedServer = Join-Path $env:USERPROFILE '.ptk\bin\PtkMcpServer.exe'
   pwsh -NoProfile -File server/test-handshake.ps1 -ServerCommand $installedServer
   ```

4. Confirm the harness can start the registered PTK server.
5. Delete only the disposable verification directory. Keep the local evidence
   until the final result has been recorded, then remove it according to the
   operator's retention preference.

## Step 6: report the result without leaking machine details

Prepare an issue #7 comment containing:

- submission ID and UTC date;
- submitted SHA-256;
- Defender signature version at reproduction;
- pre-submission protected-scan result;
- Microsoft's final determination;
- post-verdict signature version and both clean validation results;
- rebuild, reinstall, installed handshake, and harness-launch outcomes after
  removal of the incident-specific exclusions.

Do not post local paths, account email addresses, unrelated Defender
configuration, or the evidence files. Obtain the operator's explicit approval
before posting the GitHub comment or closing the issue.

Code signing is a separate release-hardening decision. Microsoft states that
consistent trusted signing helps its researchers identify a software source;
it does not guarantee that a Defender Antivirus false positive cannot recur.
If a later build with a different hash is detected, submit that exact file and
reference the earlier submission ID.
