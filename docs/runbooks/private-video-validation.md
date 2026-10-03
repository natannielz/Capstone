# Local private video validation

This recipe exercises the Windows native video helper with synthetic originals. Running it does **not** enable video uploads, approve evidence, inspect received items or change stock. The inspection owner and migration 69 now have separate integration work, described below; its current acceptance state is recorded in the [production ledger](../quality/production-progress.md). Production runtime approval, operating limits and host acceptance remain outstanding.

Local candidate 4 passed all 17 selected native storage/image/video checks, including all four `VideoDecoderTests`. Evidence is retained in `tmp/production/ocr-results/unboxing-video-native-boundary-release4.log` and `.trx`; the failed release 3 result is also retained. This establishes local helper validation only, not workflow, staging or production acceptance.

Candidate 5 retains the same domain/platform/API assemblies and adds a rejection assertion for nonempty metadata. The complete recipe below was also executed with fresh synthetic files: all four real video tests passed in `tmp/production/ocr-results/video-local-dfe346a1bcee4a209ed0b9321d1f10f8.trx`.

The implementation is in [`VideoDecoder.cs`](../../backend/src/UnitToko.Platform/DocumentProcessing/VideoDecoder.cs), [`VerifiedOriginalHandle.cs`](../../backend/src/UnitToko.Platform/Storage/VerifiedOriginalHandle.cs) and [`VideoDecoderTests.cs`](../../backend/tests/UnitToko.Tests/Documents/VideoDecoderTests.cs). `OpenVerifiedForNativeValidationAsync` supplies an opaque capability for the exact immutable publication. The helper passes a read-only, independently positioned handle to two bounded local processes; it does not pass an upload pathname. It checks container headers, every decoded video/audio frame, duration, dimensions, counts and exact original length/hash. Both executables are hash-pinned and held against replacement during use.

## Prerequisites and inspected runtime

Use native Windows x64, the already installed repository .NET SDK (`backend/global.json` pins 10.0.401), and the reviewed Release test assembly built from the same source candidate. The private fixture parent must be an existing local directory accepted by the real storage ACL/ancestor checks. The local runs use `C:\Users\User\.codex`; the fixture creates and cleans only its own marked synthetic child directories. Do not loosen ACL or link checks to obtain a pass.

The inspected local distribution is BtbN **9.0.2-22-g46d8f462ee-20261001**, linked by the FFmpeg download page, stored under ignored `.tools/video-decoder/btbn9-20261001`. Its archive provenance is retained in `download-provenance.json` beside the archive. These are local validation pins, not production approval:

| File | SHA-256 |
| --- | --- |
| `ffmpeg-n9.0-latest-win64-lgpl-9.0.zip` | `4a2a9d422e326b879e3d9443837252c29931f0dd31074ecb9a090f2961f4387a` |
| `ffmpeg-n9.0-latest-win64-lgpl-9.0/bin/ffprobe.exe` | `b364c7974d2aa8195f7e97064e658fd7f738fe4e78a6db092a3bf6cd5cc9488c` |
| `ffmpeg-n9.0-latest-win64-lgpl-9.0/bin/ffmpeg.exe` | `c61984150fbe7699cb7387f23b9e71c759d0b097dd7a61bf670b555e054d6afe` |

No command below downloads or installs a runtime. Missing files or mismatched hashes must be resolved through an inspected runtime change, not by changing the test pins to match an arbitrary binary. Use a dedicated PowerShell session from the authoritative checkout:

```powershell
$ErrorActionPreference = 'Stop'
$videoRepo = (Resolve-Path 'C:\Users\User\Documents\Capstone').Path
$videoRuntime = Join-Path $videoRepo '.tools/video-decoder/btbn9-20261001'
$videoBin = Join-Path $videoRuntime 'ffmpeg-n9.0-latest-win64-lgpl-9.0/bin'
$videoProbe = Join-Path $videoBin 'ffprobe.exe'
$videoEncoder = Join-Path $videoBin 'ffmpeg.exe'
$videoPins = @{
    (Join-Path $videoRuntime 'ffmpeg-n9.0-latest-win64-lgpl-9.0.zip') = '4a2a9d422e326b879e3d9443837252c29931f0dd31074ecb9a090f2961f4387a'
    $videoProbe = 'b364c7974d2aa8195f7e97064e658fd7f738fe4e78a6db092a3bf6cd5cc9488c'
    $videoEncoder = 'c61984150fbe7699cb7387f23b9e71c759d0b097dd7a61bf670b555e054d6afe'
}
foreach ($videoPath in $videoPins.Keys) {
    if ((Get-FileHash -LiteralPath $videoPath -Algorithm SHA256).Hash.ToLowerInvariant() -ne $videoPins[$videoPath]) {
        throw "Unexpected local runtime hash: $videoPath"
    }
}
$videoRun = 'video-local-' + [Guid]::NewGuid().ToString('N')
$videoFixtures = Join-Path $videoRepo ('tmp/production/video-fixtures/' + $videoRun)
$videoResults = Join-Path $videoRepo 'tmp/production/ocr-results'
New-Item -ItemType Directory -Path $videoFixtures | Out-Null
if (-not (Test-Path -LiteralPath $videoResults -PathType Container)) {
    throw 'The local validation results directory must already exist.'
}
$videoMp4 = Join-Path $videoFixtures 'synthetic-blue-h264-aac.mp4'
$videoWebm = Join-Path $videoFixtures 'synthetic-blue-vp9-opus.webm'
```

## Generate synthetic originals and exact probe output

The fixtures contain a blue 64×48 image at 10 fps for 0.5 seconds and a 1 kHz mono tone at 48 kHz. They contain no business evidence. Keep originals and failed outputs; the unique directory and `-n` prevent overwriting prior fixtures. Do not trim, pad or rewrite probe facts to satisfy an assertion.

```powershell
$videoInputs = @('-hide_banner', '-loglevel', 'error', '-nostdin', '-n',
    '-f', 'lavfi', '-i', 'color=c=blue:s=64x48:r=10:d=0.5',
    '-f', 'lavfi', '-i', 'sine=frequency=1000:sample_rate=48000:duration=0.5',
    '-map', '0:v:0', '-map', '1:a:0')
& $videoEncoder @videoInputs -c:v libopenh264 -pix_fmt yuv420p -threads 1 -c:a aac -ac 1 -shortest $videoMp4
if ($LASTEXITCODE -ne 0) { throw 'Synthetic MP4 generation failed.' }
& $videoEncoder @videoInputs -c:v libvpx-vp9 -threads 1 -c:a libopus -ac 1 -shortest $videoWebm
if ($LASTEXITCODE -ne 0) { throw 'Synthetic WebM generation failed.' }

# Exact Entries projection from WindowsVideoDecoder; retain all returned frames.
$videoEntries = 'format=format_name,duration,size:format_tags=major_brand:' +
    'stream=index,codec_name,codec_type,width,height,sample_rate,channels,time_base,duration_ts,duration,nb_frames:' +
    'stream_disposition=attached_pic:stream_tags=:stream_side_data=:' +
    'frame=media_type,stream_index,best_effort_timestamp_time,duration_time,width,height,nb_samples,channels:' +
    'frame_tags=:frame_side_data=:program=program_id:program_tags=:stream_group=index,id,nb_streams'
$videoProbeArgs = @('-hide_banner', '-v', 'error', '-show_frames', '-show_streams',
    '-show_format', '-show_programs', '-show_stream_groups', '-show_entries', $videoEntries,
    '-show_optional_fields', 'auto', '-noprivate', '-of', 'json=string_validation=fail')
foreach ($videoFixture in @($videoMp4, $videoWebm)) {
    $videoJson = $videoFixture + '.probe.json'
    $videoError = $videoFixture + '.probe.stderr.log'
    $videoOutput = & $videoProbe @videoProbeArgs -i $videoFixture 2> $videoError
    if ($LASTEXITCODE -ne 0 -or (Get-Item -LiteralPath $videoError).Length -ne 0) {
        throw "Synthetic probe failed: $videoFixture"
    }
    [IO.File]::WriteAllText($videoJson, ($videoOutput -join "`n"), [Text.UTF8Encoding]::new($false))
}
```

This direct filename probe generates a synthetic parser fixture only. The actual helper separately uses the verified private handle with `fd:` only, container/codec restrictions and Windows process budgets. A direct probe success does not prove that private path. Use the complete MP4 output with its matching MP4 bytes; the older `video-decoder-synthetic-probe-release1.json` has a different projection and must not be substituted. Existing local reference fixtures are `tmp/production/video-fixtures/synthetic-blue-h264-aac.mp4`, `synthetic-blue-vp9-opus.webm` and `tmp/production/ocr-results/video-decoder-synthetic-mp4-probe-release2.json`.

## Run the actual private-capability tests

All five `UNITTOKO_VIDEO_*` variables below must name existing absolute files. Windows tests deliberately fail when these are absent; the existing `WindowsStorageFact` gate skips other operating systems. A non-Windows skip is not release evidence. The focused test filter needs no Mongo service. Broader image/native storage suites retain their own prerequisites.

```powershell
$env:UNITTOKO_VIDEO_FFPROBE = $videoProbe
$env:UNITTOKO_VIDEO_FFMPEG = $videoEncoder
$env:UNITTOKO_VIDEO_FIXTURE_MP4 = $videoMp4
$env:UNITTOKO_VIDEO_FIXTURE_WEBM = $videoWebm
$env:UNITTOKO_VIDEO_PROBE_JSON = $videoMp4 + '.probe.json'
$env:UNITTOKO_EVIDENCE_TEST_PARENT = 'C:\Users\User\.codex'
$videoDotnet = Join-Path $videoRepo '.tools/dotnet10/dotnet.exe'
$videoProject = Join-Path $videoRepo 'backend/tests/UnitToko.Tests/UnitToko.Tests.csproj'
$videoLog = Join-Path $videoResults ($videoRun + '.log')

# Requires the reviewed current-source Release build; does not restore or build.
Set-Location (Join-Path $videoRepo 'backend')
& $videoDotnet test $videoProject --configuration Release --no-build --no-restore `
    --filter 'FullyQualifiedName~UnitToko.Tests.Documents.VideoDecoderTests' `
    --logger ('trx;LogFileName=' + $videoRun + '.trx') --logger 'console;verbosity=normal' `
    --results-directory $videoResults *> $videoLog
$videoExit = $LASTEXITCODE
Get-Content -LiteralPath $videoLog
if ($videoExit -ne 0) { throw 'Video checks failed; retain the log, TRX and fixtures.' }
[xml]$videoTrx = Get-Content -LiteralPath (Join-Path $videoResults ($videoRun + '.trx')) -Raw
$videoCounters = $videoTrx.TestRun.ResultSummary.Counters
if ([int]$videoCounters.total -ne 4 -or [int]$videoCounters.passed -ne 4 -or [int]$videoCounters.notExecuted -ne 0) {
    throw 'Expected all four Windows video tests to run and pass.'
}
```

The four tests cover real private MP4/WebM decoding, publication/hash mismatches and malformed originals, complete-frame/aggregate-audio validation, and cancellation/deadline cleanup of native handles and binary pins. Expected synthetic facts are five video frames, one 48 kHz audio channel, 24,000 decoded scalar audio samples, and 24 AAC frames or 26 Opus frames, with measured duration between 0.5 and 0.51 seconds. These are fixture expectations, not measurements of user uploads.

The test-only limits are 1,000,000 input bytes, 128×128/16,384 pixels, one second, 20 video frames, two audio channels, 48 kHz, 48,000 scalar samples and 100 audio frames. The test process budget is 256 MiB, 20 seconds and 256 KiB probe output. They must not become automatic production defaults. Job resource confinement does not establish a restricted service identity, denied network access, scanner acceptance or permission to access unrelated files.

Retain each candidate's source/assembly hashes, complete log/TRX and original fixtures. The inspected runtime and successful direct probe do not by themselves establish a passing helper test; earlier failed native results remain evidence. Claim local helper validation only after the exact current-source Windows test run passes without skips. Upload admission, owner binding, worker/policy integration, migration/restore and actual host/provider acceptance require their own later results.

## Procurement inspection integration

The `/procurement/inspections` workspace uses actual supplier-notice/container-intake history. A draft records the exact purchase-order package definition, observed package counts, lot/expiry/putaway details when known, and discrepancies. Damaged, expired and opened counts can overlap. They are observations, not a calculated quantity of healthy stock. Submission consumes the selected intake's remaining container-inspection capacity and remains `submitted_quarantine`; it neither releases items for sale nor posts money.

The author needs current scoped `procurement.read`, `procurement.inspection.record` and `procurement.inspection.evidence.read` authority, including the record permission's fresh MFA. Separate readers require the applicable read grants. A draft binds its original author; submitting or viewing an unrelated reservation is denied. No new grant is installed automatically.

Before submission, the exact original must be uploaded under `ProcurementUnboxingVideo`, scanned and fully decoded by the private worker, then verified and pinned in the owner's transaction. Upload completion alone is insufficient. The video remains private; reads recheck current authority, validation policy and retention under a finite lease. A successful media verdict does not establish what the video depicts. Operators still review the actual receipt and discrepancies.

Migration 69 preserves prior image/payment/refund/disbursement evidence branches and adds separate video admission, read and validation policy stores. It checks retained histories before changing guards and seeds no approvals. Existing image byte limits remain unchanged. Missing or expired video approvals leave the dependent operations unavailable.

| Host input | Required operational decision |
| --- | --- |
| API `Evidence:PrivateVideoWindows` | Private root, store identity and trusted operator SIDs accepted on the actual host |
| Worker `Evidence:VideoValidation` | Same exact store plus consumer identity, host acceptance reference, scanner configuration acceptance reference and claim policy for `evidence.video-validation.requested` |
| `Limits` and `Decoder` | Explicit finite byte, duration, video/audio dimensions/counts, process time/memory/output limits and approved executable hashes |
| `Scanner`, `AcceptedClamdStreamCeiling` | Accepted local scanner endpoint, current real signatures and an independently accepted clamd stream ceiling covering the configured video byte limit |
| `evidenceVideoStorePolicies`, `evidenceVideoReadPolicies`, `evidenceVideoValidationPolicies` | Current approved store quotas, verification/read budgets and exact worker pipeline identity |

Do not copy synthetic fixture approval references into these policies. The local workflow tests use real Mongo, private files and native decoders, but a loopback scanner protocol fixture; they do not accept a production antivirus installation. The real host still needs service-identity, ACL, scanner/signature, resource, network-isolation, retention and restore acceptance.

The video upload endpoint obtains its currently authorized finite byte cap before reading the request, then applies both server and stream limits. There is no global unlimited body setting. Inspect the exact API/worker deployment configuration and current policy records before operational use; source publication and local fixtures do not establish those connections.
