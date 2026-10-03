using System.Diagnostics;
using System.Runtime.Versioning;
using System.Runtime.InteropServices;
using System.Reflection;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;
using UnitToko.Platform.DocumentProcessing;
using UnitToko.Platform.Storage;

namespace UnitToko.Tests.Documents;

[SupportedOSPlatform("windows")]
public sealed class PrivateEvidenceStorageTests
{
    private const string StoreId = "1111111111111111111111111111111111111111111111111111111111111111";
    private const string OtherStore = "9999999999999999999999999999999999999999999999999999999999999999";
    private const string StageKey = "2222222222222222222222222222222222222222222222222222222222222222";
    private const string Generation = "3333333333333333333333333333333333333333333333333333333333333333";
    private const string AbcHash = "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad";
    private static StorageAttempt Attempt(string store = StoreId) => new(store, 1, 2, 1, StageKey, Generation);
    private static ExpectedPublication Expected(StorageAttempt? attempt = null) =>
        new(attempt ?? Attempt(), 1, new StorageContent(3, AbcHash));
    private static StorageIoBudget Budget(long bytes = 100) => new(bytes, TimeSpan.FromSeconds(10), 128);

    [WindowsStorageFact]
    public async Task Complete_stage_publish_and_exact_read_preserve_original_bytes()
    {
        using var root = new StorageRoot();
        var store = root.Open();
        using var input = new MemoryStream("abc"u8.ToArray());
        var staged = Assert.IsType<StageOutcome.Sealed>(await store.StageAsync(Attempt(), input, Budget(), default));
        Assert.Equal(3, staged.Stage.Content.Length);
        Assert.Equal(AbcHash, staged.Stage.Content.Sha256);
        Assert.True(input.CanRead);
        Assert.Equal("abc", File.ReadAllText(root.Ready));
        Assert.Equal(PublishState.Published, (await store.PublishAsync(Expected(), Budget(), default)).State);
        var opened = Assert.IsType<OpenOutcome.Opened>(await store.OpenVerifiedAsync(Expected(), Budget(), default));
        await using (opened.Handle)
        {
            Assert.False(opened.Handle.Stream.CanWrite);
            Assert.False(opened.Handle.Stream.CanSeek);
            using var output = new MemoryStream();
            await opened.Handle.Stream.CopyToAsync(output);
            Assert.Equal("abc"u8.ToArray(), output.ToArray());
            Assert.Throws<NotSupportedException>(() => opened.Handle.Stream.WriteByte(1));
        }
        Assert.Equal("abc", File.ReadAllText(root.Original));
    }

    [WindowsStorageFact]
    public async Task Repeated_stage_does_not_consume_replacement_source()
    {
        using var root = new StorageRoot();
        var store = root.Open();
        Assert.IsType<StageOutcome.Sealed>(await store.StageAsync(Attempt(), new MemoryStream("abc"u8.ToArray()), Budget(), default));
        using var replacement = new TrackingSource("xyz"u8.ToArray());
        Assert.IsType<StageOutcome.ExistingAttempt>(await store.StageAsync(Attempt(), replacement, Budget(), default));
        Assert.Equal(0, replacement.Reads);
        Assert.Equal("abc", File.ReadAllText(root.Part));
        Assert.Equal("abc", File.ReadAllText(root.Ready));
    }

    [WindowsStorageFact]
    public async Task Existing_ready_without_part_also_rejects_replacement_source()
    {
        using var root = new StorageRoot();
        File.WriteAllText(root.Ready, "abc");
        using var replacement = new TrackingSource("xyz"u8.ToArray());
        Assert.IsType<StageOutcome.ExistingAttempt>(await root.Open().StageAsync(Attempt(), replacement, Budget(), default));
        Assert.Equal(0, replacement.Reads);
        Assert.False(File.Exists(root.Part));
    }

    [WindowsStorageTheory]
    [InlineData("", 3)]
    [InlineData("abcd", 3)]
    public async Task Empty_or_oversized_source_never_seals(string input, long limit)
    {
        using var root = new StorageRoot();
        var result = await root.Open().StageAsync(Attempt(), new MemoryStream(Encoding.UTF8.GetBytes(input)), Budget(limit), default);
        Assert.IsType<StageOutcome.Interrupted>(result);
        Assert.True(File.Exists(root.Part));
        Assert.True(new FileInfo(root.Part).Length <= limit);
        Assert.False(File.Exists(root.Ready));
        Assert.False(File.Exists(root.Original));
    }

    [WindowsStorageFact]
    public async Task Exact_byte_limit_with_unknown_length_can_complete()
    {
        using var root = new StorageRoot();
        using var input = new TrackingSource("abc"u8.ToArray());
        Assert.IsType<StageOutcome.Sealed>(await root.Open().StageAsync(Attempt(), input, Budget(3), default));
        Assert.Equal("abc", File.ReadAllText(root.Ready));
    }

    [WindowsStorageFact]
    public async Task Source_failure_preserves_partial_without_ready()
    {
        using var root = new StorageRoot();
        using var input = new TrackingSource("ab"u8.ToArray(), throwAfterBytes: true);
        Assert.IsType<StageOutcome.Interrupted>(await root.Open().StageAsync(Attempt(), input, Budget(), default));
        Assert.Equal("ab", File.ReadAllText(root.Part));
        Assert.False(File.Exists(root.Ready));
    }

    [WindowsStorageFact]
    public async Task Cancellation_before_creation_has_no_storage_effect()
    {
        using var root = new StorageRoot();
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        using var input = new TrackingSource("abc"u8.ToArray());
        Assert.IsType<StageOutcome.Rejected>(await root.Open().StageAsync(Attempt(), input, Budget(), cancelled.Token));
        Assert.Equal(0, input.Reads);
        Assert.False(File.Exists(root.Part));
    }

    [WindowsStorageFact]
    public async Task Deadline_cancels_cooperative_source_and_preserves_attempt()
    {
        using var root = new StorageRoot();
        using var input = new WaitingSource();
        var budget = new StorageIoBudget(10, TimeSpan.FromMilliseconds(1000), 16);
        Assert.IsType<StageOutcome.Interrupted>(await root.Open().StageAsync(Attempt(), input, budget, default));
        Assert.True(File.Exists(root.Part));
        Assert.False(File.Exists(root.Ready));
    }

    [WindowsStorageFact]
    public async Task Wrong_store_ticket_never_consumes_input_or_creates_files()
    {
        using var root = new StorageRoot();
        using var source = new TrackingSource("abc"u8.ToArray());
        Assert.IsType<StageOutcome.Rejected>(await root.Open().StageAsync(Attempt(OtherStore), source, Budget(), default));
        Assert.Equal(0, source.Reads);
        Assert.False(File.Exists(root.Part));
    }

    [WindowsStorageFact]
    public async Task Corrupted_sealed_stage_cannot_publish()
    {
        using var root = new StorageRoot();
        var store = root.Open();
        await store.StageAsync(Attempt(), new MemoryStream("abc"u8.ToArray()), Budget(), default);
        File.WriteAllText(root.Ready, "xyz");
        Assert.Equal(PublishState.Conflict, (await store.PublishAsync(Expected(), Budget(), default)).State);
        Assert.False(File.Exists(root.Original));
        Assert.Equal("xyz", File.ReadAllText(root.Ready));
    }

    [WindowsStorageTheory]
    [InlineData("a", (int)PublishState.Incomplete)]
    [InlineData("abcd", (int)PublishState.Conflict)]
    [InlineData("xyz", (int)PublishState.Conflict)]
    public async Task Existing_partial_or_mismatched_final_is_never_overwritten(string existing, int expected)
    {
        using var root = new StorageRoot();
        File.WriteAllText(root.Original, existing);
        var result = await root.Open().PublishAsync(Expected(), Budget(), default);
        Assert.Equal((PublishState)expected, result.State);
        Assert.Equal(existing, File.ReadAllText(root.Original));
    }

    [WindowsStorageFact]
    public async Task Full_matching_final_recovers_lost_response_without_stage_or_marker()
    {
        using var root = new StorageRoot();
        File.WriteAllText(root.Original, "abc");
        Assert.Equal(PublishState.ExistingIdentical, (await root.Open().PublishAsync(Expected(), Budget(), default)).State);
        Assert.Equal(InspectionState.Matching, (await root.Open().InspectOriginalAsync(Expected(), Budget(), default)).State);
        Assert.False(File.Exists(root.Ready));
    }

    [WindowsStorageFact]
    public async Task Concurrent_publishers_never_replace_the_created_generation()
    {
        using var root = new StorageRoot();
        var first = root.Open();
        var second = root.Open();
        await first.StageAsync(Attempt(), new MemoryStream("abc"u8.ToArray()), Budget(), default);
        var results = await Task.WhenAll(
            Task.Run(async () => await first.PublishAsync(Expected(), Budget(), default)),
            Task.Run(async () => await second.PublishAsync(Expected(), Budget(), default)));
        Assert.Single(results, result => result.State == PublishState.Published);
        Assert.All(results, result => Assert.Contains(result.State,
            new[] { PublishState.Published, PublishState.ExistingIdentical, PublishState.Unknown }));
        Assert.Equal("abc", File.ReadAllText(root.Original));
        Assert.Equal(PublishState.ExistingIdentical, (await second.PublishAsync(Expected(), Budget(), default)).State);
    }

    [WindowsStorageFact]
    public async Task Held_verified_read_denies_write_and_delete_until_disposal()
    {
        using var root = new StorageRoot();
        File.WriteAllText(root.Original, "abc");
        var opened = Assert.IsType<OpenOutcome.Opened>(await root.Open().OpenVerifiedAsync(Expected(), Budget(), default));
        Assert.Throws<IOException>(() => File.Open(root.Original, FileMode.Open, FileAccess.Read, FileShare.Read).Dispose());
        Assert.Throws<IOException>(() => File.Open(root.Original, FileMode.Open, FileAccess.Write, FileShare.ReadWrite).Dispose());
        Assert.Throws<IOException>(() => File.Delete(root.Original));
        await opened.Handle.DisposeAsync();
        Assert.Equal("abc", File.ReadAllText(root.Original));
        Assert.Throws<ObjectDisposedException>(() => opened.Handle.Stream.ReadByte());
    }

    [WindowsStorageFact]
    public async Task Native_capability_preserves_exact_original_identity_readonly_inheritance_and_independent_seek_positions()
    {
        using var root = new StorageRoot(); var store = root.Open();
        Assert.IsType<StageOutcome.Sealed>(await store.StageAsync(Attempt(), new MemoryStream("abc"u8.ToArray()), Budget(), default));
        Assert.Equal(PublishState.Published, (await store.PublishAsync(Expected(), Budget(), default)).State);
        var opened = Assert.IsType<NativeOpenOutcome.Opened>(await store.OpenVerifiedForNativeValidationAsync(Expected(), Budget(), default));
        await using var capability = opened.Handle;
        Assert.Equal(Expected(), capability.Publication); Assert.Equal(Expected().Content, capability.Content); Assert.Equal(Attempt(), capability.Attempt);
        Assert.Empty(typeof(VerifiedNativeOriginalHandle).GetConstructors(BindingFlags.Public | BindingFlags.Instance));
        Assert.Equal(new[] { "Attempt", "Content", "Publication" }, typeof(VerifiedNativeOriginalHandle).GetProperties(BindingFlags.Public | BindingFlags.Instance).Select(property => property.Name).Order().ToArray());
        Assert.DoesNotContain(typeof(VerifiedNativeOriginalHandle).GetProperties(BindingFlags.Public | BindingFlags.Instance), property => property.SetMethod is not null);
        string identity;
        using (var inspect = File.Open(root.Original, FileMode.Open, FileAccess.Read, FileShare.Read)) identity = NativeIdentity(inspect.SafeFileHandle);
        Assert.Throws<IOException>(() => File.Open(root.Original, FileMode.Open, FileAccess.Write, FileShare.ReadWrite).Dispose());
        Assert.Throws<IOException>(() => File.Delete(root.Original));
        for (var run = 0; run < 2; run++) {
            var result = await NativeRun(root, capability, "verify");
            Assert.Equal("exited", result.Status); Assert.Equal(0, result.ExitCode); Assert.Equal(3, result.InputLength); Assert.Equal(AbcHash, result.InputSha256);
            using var parsed = JsonDocument.Parse(result.Output); AssertNativeProof(parsed.RootElement, identity);
            Assert.True(result.PeakJobMemoryBytes is > 0 and <= 268435456);
            AssertProcessStopped(parsed.RootElement.GetProperty("pid").GetInt32());
            File.Delete(System.IO.Path.Combine(root.Path, "native-ready.json"));
        }
        await capability.DisposeAsync();
        using (var writable = File.Open(root.Original, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) Assert.Equal(3, writable.Length);
        Assert.Equal("abc", File.ReadAllText(root.Original));
    }

    [WindowsStorageFact]
    public async Task Native_capability_rejects_corrupt_original_budget_tuple_or_disposal_before_spawning()
    {
        using var root = new StorageRoot(); var store = root.Open(); File.WriteAllText(root.Original, "abc");
        var corrupt = new ExpectedPublication(Attempt(), 1, new StorageContent(3, new string('0', 64)));
        var denied = Assert.IsType<NativeOpenOutcome.Rejected>(await store.OpenVerifiedForNativeValidationAsync(corrupt, Budget(), default));
        Assert.Equal(InspectionState.Conflict, denied.Inspection.State);
        Assert.IsType<NativeOpenOutcome.Rejected>(await store.OpenVerifiedForNativeValidationAsync(Expected(), Budget(2), default));
        Assert.IsType<NativeOpenOutcome.Rejected>(await store.OpenVerifiedForNativeValidationAsync(Expected(Attempt(OtherStore)), Budget(), default));
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        Assert.IsType<NativeOpenOutcome.Rejected>(await store.OpenVerifiedForNativeValidationAsync(Expected(), Budget(), canceled.Token));
        var opened = Assert.IsType<NativeOpenOutcome.Opened>(await store.OpenVerifiedForNativeValidationAsync(Expected(), Budget(), default));
        await using var capability = opened.Handle;
        foreach (var expected in new[] { corrupt, new ExpectedPublication(Attempt(), 2, Expected().Content), Expected(Attempt(OtherStore)) })
            Assert.Equal("invalid_media", (await NativeRun(root, capability, "verify", expected: expected)).Status);
        Assert.Equal("invalid_media", (await NativeRun(root, capability, "verify", maximumBytes: 0)).Status);
        Assert.Equal("invalid_media", (await NativeRun(root, capability, "verify", maximumBytes: 2)).Status);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => NativeRun(root, capability, "verify", canceled.Token));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => NativeRun(root, capability, "verify", maximumOutput: 16 * 1024 * 1024 + 1));
        using var streamed = new MemoryStream("abc"u8.ToArray());
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => WindowsBoundedProcess.RunAsync("unused-before-admission", [],
            256 * 1024 * 1024, TimeSpan.FromSeconds(20), streamed, 3, default, 128 * 1024 + 1));
        Assert.Equal(0, streamed.Position);
        await capability.DisposeAsync();
        Assert.Equal("invalid_media", (await NativeRun(root, capability, "verify")).Status);
        Assert.False(File.Exists(System.IO.Path.Combine(root.Path, "native-ready.json")));
        using var unlocked = File.Open(root.Original, FileMode.Open, FileAccess.ReadWrite, FileShare.None); Assert.Equal(3, unlocked.Length);
    }

    [WindowsStorageFact]
    public async Task Native_child_retains_original_after_caller_disposal_and_job_cleans_up_cancellation_deadline_and_output_overflow()
    {
        foreach (var mode in new[] { "hold", "cancel", "timeout", "flood" }) {
            using var root = new StorageRoot(); File.WriteAllText(root.Original, "abc");
            var opened = Assert.IsType<NativeOpenOutcome.Opened>(await root.Open().OpenVerifiedForNativeValidationAsync(Expected(), Budget(), default));
            await using var capability = opened.Handle; using var cancellation = new CancellationTokenSource();
            var run = NativeRun(root, capability, mode == "flood" ? "flood" : "hold", cancellation.Token,
                timeout: mode == "timeout" ? TimeSpan.FromSeconds(6) : TimeSpan.FromSeconds(20), maximumOutput: mode == "flood" ? 64 : 4096);
            int? pid = null;
            try {
                using var ready = await NativeReady(root, run); pid = ready.RootElement.GetProperty("pid").GetInt32();
                Assert.True(ready.RootElement.GetProperty("inJob").GetBoolean()); Assert.False(run.IsCompleted);
                await capability.DisposeAsync();
                Assert.Throws<IOException>(() => File.Open(root.Original, FileMode.Open, FileAccess.Write, FileShare.ReadWrite).Dispose());
                Assert.Throws<IOException>(() => File.Delete(root.Original));
                if (mode == "cancel") {
                    cancellation.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
                } else {
                    var finishing = Stopwatch.StartNew();
                    if (mode != "timeout") File.WriteAllText(System.IO.Path.Combine(root.Path, "native-release"), "release");
                    var result = await run;
                    Assert.Equal(mode switch { "timeout" => "decode_timeout", "flood" => "decoder_error", _ => "exited" }, result.Status);
                    if (mode == "hold") { Assert.Equal(0, result.ExitCode); Assert.Equal(AbcHash, result.InputSha256); }
                    else { Assert.Equal(-1, result.ExitCode); Assert.Equal(0, result.InputLength); Assert.Null(result.InputSha256); }
                    if (mode == "flood") Assert.True(finishing.Elapsed < TimeSpan.FromSeconds(8), "Output overflow waited for the overall child deadline");
                }
                AssertProcessStopped(pid.Value);
                using (var unlocked = File.Open(root.Original, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) Assert.Equal(3, unlocked.Length);
                Assert.Equal("abc", File.ReadAllText(root.Original)); File.Delete(root.Original); Assert.False(File.Exists(root.Original));
            } finally {
                cancellation.Cancel(); try { await run; } catch (OperationCanceledException) { }
                if (pid.HasValue) AssertProcessStopped(pid.Value);
            }
        }
    }

    private static Task<BoundedProcessResult> NativeRun(StorageRoot root, VerifiedNativeOriginalHandle capability, string mode, CancellationToken ct = default,
        ExpectedPublication? expected = null, long maximumBytes = 3, TimeSpan? timeout = null, int maximumOutput = 4096) {
        var runtime = Environment.GetEnvironmentVariable("UNITTOKO_DECODER_DOTNET") ?? throw new InvalidOperationException("Set the approved local .NET runtime for actual native handle tests");
        var probe = System.IO.Path.Combine(AppContext.BaseDirectory, "PersistenceProbe", "UnitToko.PersistenceProbe.dll");
        Assert.True(File.Exists(probe));
        return WindowsBoundedProcess.RunAsync(runtime, [probe, "native-private-input", mode, root.Path], 256 * 1024 * 1024,
            timeout ?? TimeSpan.FromSeconds(20), capability, expected ?? Expected(), maximumBytes, ct, maximumOutput);
    }
    private static async Task<JsonDocument> NativeReady(StorageRoot root, Task<BoundedProcessResult> run) {
        var path = System.IO.Path.Combine(root.Path, "native-ready.json"); var started = Stopwatch.StartNew();
        while (!File.Exists(path)) {
            if (run.IsCompleted) { var result = await run; throw new InvalidOperationException($"Native test child stopped before ready: {result.Status}/{result.ExitCode}/{result.Error}"); }
            if (started.Elapsed > TimeSpan.FromSeconds(15)) throw new TimeoutException("Native test child did not signal readiness");
            await Task.Delay(20);
        }
        return JsonDocument.Parse(await File.ReadAllTextAsync(path));
    }
    private static void AssertNativeProof(JsonElement proof, string identity) {
        Assert.Equal(0, proof.GetProperty("initialPosition").GetInt64()); Assert.Equal(3, proof.GetProperty("length").GetInt64());
        Assert.Equal((int)'c', proof.GetProperty("last").GetInt32()); Assert.Equal(AbcHash, proof.GetProperty("hash").GetString());
        Assert.Equal(identity, proof.GetProperty("identity").GetString()); Assert.True(proof.GetProperty("inJob").GetBoolean());
        Assert.False(proof.GetProperty("writeSucceeded").GetBoolean()); Assert.Equal(0u, proof.GetProperty("written").GetUInt32()); Assert.Equal(5, proof.GetProperty("writeError").GetInt32());
    }
    private static void AssertProcessStopped(int pid) {
        try { using var process = Process.GetProcessById(pid); Assert.True(process.WaitForExit(2000), "Bounded native child survived cleanup"); }
        catch (ArgumentException) { /* An exited child may already be absent from the process table. */ }
    }
    private static string NativeIdentity(SafeFileHandle handle) {
        Assert.True(GetFileInformationByHandle(handle, out var info)); return $"{info.VolumeSerialNumber:x8}:{info.FileIndexHigh:x8}:{info.FileIndexLow:x8}";
    }
    [StructLayout(LayoutKind.Sequential)] private struct NativeFileInformation {
        public uint Attributes, CreationLow, CreationHigh, AccessLow, AccessHigh, WriteLow, WriteHigh, VolumeSerialNumber, SizeHigh, SizeLow, Links, FileIndexHigh, FileIndexLow;
    }
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetFileInformationByHandle(SafeFileHandle handle, out NativeFileInformation information);

    [WindowsStorageFact]
    public async Task Failed_integrity_open_returns_no_stream()
    {
        using var root = new StorageRoot();
        File.WriteAllText(root.Original, "xyz");
        var rejected = Assert.IsType<OpenOutcome.Rejected>(await root.Open().OpenVerifiedAsync(Expected(), Budget(), default));
        Assert.Equal(InspectionState.Conflict, rejected.Inspection.State);
    }

    [WindowsStorageFact]
    public async Task Busy_writer_is_unknown_not_absent_or_incomplete()
    {
        using var root = new StorageRoot();
        using var writer = new FileStream(root.Original, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        await writer.WriteAsync("a"u8.ToArray());
        Assert.Equal(InspectionState.Unknown, (await root.Open().InspectOriginalAsync(Expected(), Budget(), default)).State);
    }

    [WindowsStorageFact]
    public async Task Expected_tuple_larger_than_budget_cannot_publish()
    {
        using var root = new StorageRoot();
        var store = root.Open();
        await store.StageAsync(Attempt(), new MemoryStream("abc"u8.ToArray()), Budget(), default);
        Assert.Equal(PublishState.Unavailable, (await store.PublishAsync(Expected(), Budget(2), default)).State);
        Assert.False(File.Exists(root.Original));
    }

    [WindowsStorageFact]
    public void Unsafe_root_acl_is_denied()
    {
        using var root = new StorageRoot();
        var acl = new DirectoryInfo(root.Path).GetAccessControl();
        acl.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.WorldSid, null),
            FileSystemRights.ReadAndExecute, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
            PropagationFlags.None, AccessControlType.Allow));
        new DirectoryInfo(root.Path).SetAccessControl(acl);
        Assert.Null(root.Activation().Store);
    }

    [WindowsStorageFact]
    public void Unsafe_parent_delete_child_right_is_denied_even_with_private_leaf()
    {
        using var root = new StorageRoot();
        var child = System.IO.Path.Combine(root.Path, "private");
        StorageRoot.CreatePrivateDirectory(child);
        var acl = new DirectoryInfo(root.Path).GetAccessControl();
        acl.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.WorldSid, null),
            FileSystemRights.DeleteSubdirectoriesAndFiles, AccessControlType.Allow));
        new DirectoryInfo(root.Path).SetAccessControl(acl);
        Assert.Null(WindowsLocalEvidenceStore.TryOpen(child, StoreId, StorageRoot.TrustedOperators).Store);
    }

    [WindowsStorageFact]
    public async Task Changed_acl_after_activation_stops_upload()
    {
        using var root = new StorageRoot();
        var store = root.Open();
        var acl = new DirectoryInfo(root.Path).GetAccessControl();
        acl.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.WorldSid, null),
            FileSystemRights.ReadData, AccessControlType.Allow));
        new DirectoryInfo(root.Path).SetAccessControl(acl);
        Assert.IsType<StageOutcome.Unavailable>(await store.StageAsync(Attempt(), new MemoryStream("abc"u8.ToArray()), Budget(), default));
        Assert.False(File.Exists(root.Part));
    }

    [WindowsStorageFact]
    public async Task Reparse_leaf_is_rejected_without_following_target()
    {
        using var root = new StorageRoot();
        var target = System.IO.Path.Combine(root.Path, "unrelated");
        StorageRoot.CreatePrivateDirectory(target);
        File.WriteAllText(System.IO.Path.Combine(target, "sentinel"), "protected");
        StorageRoot.CreateJunction(root.Part, target);
        Assert.IsType<StageOutcome.Unavailable>(await root.Open().StageAsync(Attempt(), new MemoryStream("abc"u8.ToArray()), Budget(), default));
        Assert.Equal("protected", File.ReadAllText(System.IO.Path.Combine(target, "sentinel")));
    }

    [WindowsStorageFact]
    public void Reparse_ancestor_is_denied()
    {
        using var root = new StorageRoot();
        var real = System.IO.Path.Combine(root.Path, "real");
        StorageRoot.CreatePrivateDirectory(real);
        var link = System.IO.Path.Combine(root.Path, "linked");
        StorageRoot.CreateJunction(link, real);
        Assert.Null(WindowsLocalEvidenceStore.TryOpen(link, StoreId, StorageRoot.TrustedOperators).Store);
    }

    [WindowsStorageFact]
    public async Task Unavailable_default_never_consumes_source()
    {
        using var source = new TrackingSource("abc"u8.ToArray());
        IPrivateEvidenceStore store = new UnavailablePrivateEvidenceStore();
        Assert.IsType<StageOutcome.Unavailable>(await store.StageAsync(Attempt(), source, Budget(), default));
        Assert.Equal(0, source.Reads);
        Assert.Equal(PublishState.Unavailable, (await store.PublishAsync(Expected(), Budget(), default)).State);
    }

    [WindowsStorageTheory]
    [InlineData("../bad")]
    [InlineData("C:\\escape")]
    [InlineData("file:stream")]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    [InlineData("")]
    public void Noncanonical_storage_tokens_are_rejected(string token) =>
        Assert.Throws<ArgumentException>(() => new StorageAttempt(StoreId, 1, 2, 1, token, Generation));

    [WindowsStorageTheory]
    [InlineData(0, 1, 1)]
    [InlineData(1, 0, 1)]
    [InlineData(1, 1, 0)]
    public void Nonpositive_durable_ticket_identity_is_rejected(long reservation, long attempt, long revision) =>
        Assert.Throws<ArgumentException>(() => new StorageAttempt(StoreId, reservation, attempt, revision, StageKey, Generation));

    [WindowsStorageTheory]
    [InlineData(0, 10, 128)]
    [InlineData(10, 0, 128)]
    [InlineData(10, 10, 0)]
    [InlineData(10, 10, 1048577)]
    public void Unbounded_or_invalid_budget_is_rejected(long bytes, int milliseconds, int buffer) =>
        Assert.Throws<ArgumentException>(() => new StorageIoBudget(bytes, TimeSpan.FromMilliseconds(milliseconds), buffer));


    [WindowsStorageTheory]
    [InlineData("S-1-1-0")]
    [InlineData("S-1-5-11")]
    [InlineData("S-1-5-32-545")]
    [InlineData("S-1-5-32-546")]
    [InlineData("S-1-5-7")]
    [InlineData("S-1-5-4")]
    [InlineData("S-1-5-2")]
    [InlineData("S-1-5-14")]
    [InlineData("S-1-15-2-1")]
    [InlineData("S-1-5-80-0")]
    [InlineData("S-1-5-21-111-222-333-513")]
    public void Unsupported_principal_cannot_be_configured_as_a_trusted_operator(string broadSid)
    {
        using var root = new StorageRoot();
        var acl = new DirectoryInfo(root.Path).GetAccessControl();
        acl.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(broadSid),
            FileSystemRights.ReadAndExecute, AccessControlType.Allow));
        new DirectoryInfo(root.Path).SetAccessControl(acl);
        Assert.Null(WindowsLocalEvidenceStore.TryOpen(root.Path, StoreId,
            [.. StorageRoot.TrustedOperators, broadSid]).Store);
    }

    [WindowsStorageFact]
    public async Task Inspect_stage_requires_exact_ready_bytes_and_committed_tuple()
    {
        using var root = new StorageRoot();
        var store = root.Open();
        Assert.Equal(InspectionState.Absent, (await store.InspectStageAsync(Expected(), Budget(), default)).State);
        File.WriteAllText(root.Ready, "a");
        Assert.Equal(InspectionState.Incomplete, (await store.InspectStageAsync(Expected(), Budget(), default)).State);
        File.WriteAllText(root.Ready, "xyz");
        Assert.Equal(InspectionState.Conflict, (await store.InspectStageAsync(Expected(), Budget(), default)).State);
        File.WriteAllText(root.Ready, "abc");
        Assert.Equal(InspectionState.Matching, (await store.InspectStageAsync(Expected(), Budget(), default)).State);
        Assert.Equal(InspectionState.Unavailable, (await store.InspectStageAsync(null!, Budget(), default)).State);
    }

    [WindowsStorageFact]
    public async Task Unsafe_file_acl_returns_no_verified_stream()
    {
        using var root = new StorageRoot();
        File.WriteAllText(root.Original, "abc");
        var acl = new FileInfo(root.Original).GetAccessControl();
        acl.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.WorldSid, null),
            FileSystemRights.ReadData, AccessControlType.Allow));
        new FileInfo(root.Original).SetAccessControl(acl);
        var result = await root.Open().OpenVerifiedAsync(Expected(), Budget(), default);
        if (result is OpenOutcome.Opened unexpected) await unexpected.Handle.DisposeAsync();
        var outcome = Assert.IsType<OpenOutcome.Rejected>(result);
        Assert.Equal(InspectionState.Unavailable, outcome.Inspection.State);
        var native = await root.Open().OpenVerifiedForNativeValidationAsync(Expected(), Budget(), default);
        if (native is NativeOpenOutcome.Opened unexpectedNative) await unexpectedNative.Handle.DisposeAsync();
        Assert.Equal(InspectionState.Unavailable, Assert.IsType<NativeOpenOutcome.Rejected>(native).Inspection.State);
        Assert.Equal("abc", File.ReadAllText(root.Original));
    }


    [WindowsStorageFact]
    public async Task Unexpected_source_exception_returns_interrupted_and_retains_partial()
    {
        using var root = new StorageRoot();
        var sourceFailure = new InvalidOperationException("Injected source failure.");
        using var input = new TrackingSource("ab"u8.ToArray(), failure: sourceFailure);
        var outcome = Assert.IsType<StageOutcome.Interrupted>(
            await root.Open().StageAsync(Attempt(), input, Budget(), default));
        Assert.Same(sourceFailure, outcome.Failure!.InnerException);
        Assert.Equal("ab", File.ReadAllText(root.Part));
        Assert.False(File.Exists(root.Ready));
    }


    [WindowsStorageFact]
    public async Task Untrusted_file_execute_right_cannot_be_treated_as_directory_traversal()
    {
        using var root = new StorageRoot();
        File.WriteAllText(root.Original, "abc");
        var acl = new FileInfo(root.Original).GetAccessControl();
        acl.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.WorldSid, null),
            FileSystemRights.ExecuteFile, AccessControlType.Allow));
        new FileInfo(root.Original).SetAccessControl(acl);
        var result = await root.Open().OpenVerifiedAsync(Expected(), Budget(), default);
        if (result is OpenOutcome.Opened unexpected) await unexpected.Handle.DisposeAsync();
        var outcome = Assert.IsType<OpenOutcome.Rejected>(result);
        Assert.Equal(InspectionState.Unavailable, outcome.Inspection.State);
    }

    internal sealed class StorageRoot : IDisposable
    {
        private readonly string parent;
        private readonly string marker = Guid.NewGuid().ToString("N");
        public string Path { get; }
        public string Part => System.IO.Path.Combine(Path, StageKey + ".part");
        public string Ready => System.IO.Path.Combine(Path, StageKey + ".ready");
        public string Original => System.IO.Path.Combine(Path, Generation + ".original");
        public static string[] TrustedOperators => [
            "S-1-5-18", "S-1-5-32-544",
            "S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464"
        ];

        public StorageRoot()
        {
            var configured = Environment.GetEnvironmentVariable("UNITTOKO_EVIDENCE_TEST_PARENT");
            if (configured is null) {
                parent = System.IO.Path.GetFullPath(System.IO.Path.Combine(AppContext.BaseDirectory, "../../../../../"));
                if (!File.Exists(System.IO.Path.Combine(parent, "Directory.Build.props")))
                    throw new InvalidOperationException("Fixture parent is not the isolated backend checkout.");
            } else {
                if (!System.IO.Path.IsPathFullyQualified(configured) || !Directory.Exists(configured))
                    throw new InvalidOperationException("Evidence test parent must be an existing absolute directory.");
                parent = System.IO.Path.GetFullPath(configured);
            }
            // Only synthetic fixture bytes move. The production store still validates the
            // actual private leaf and every ancestor; no ACL or trusted-operator rule changes.
            for (DirectoryInfo? ancestor = new(parent); ancestor is not null; ancestor = ancestor.Parent)
                if ((ancestor.Attributes & FileAttributes.ReparsePoint) != 0) throw new InvalidOperationException("Evidence test parent contains a link.");
            Path = System.IO.Path.Combine(parent, ".storage-test-" + marker);
            if (Directory.Exists(Path)) throw new InvalidOperationException("Fixture collision.");
            CreatePrivateDirectory(Path);
            File.WriteAllText(System.IO.Path.Combine(Path, ".fixture-id"), marker);
            File.WriteAllText(System.IO.Path.Combine(Path, ".fixture-parent"), parent);
        }

        public static void CreatePrivateDirectory(string path)
        {
            var security = new DirectorySecurity();
            var user = WindowsIdentity.GetCurrent().User!;
            security.SetOwner(user);
            security.SetAccessRuleProtection(true, false);
            foreach (var sid in new[] { user, new SecurityIdentifier("S-1-5-18"), new SecurityIdentifier("S-1-5-32-544") })
                security.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl,
                    InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
            new DirectoryInfo(path).Create(security);
        }

        public static void CreateJunction(string link, string target)
        {
            var start = new ProcessStartInfo("cmd.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var argument in new[] { "/c", "mklink", "/J", link, target }) start.ArgumentList.Add(argument);
            using var process = Process.Start(start)!;
            var output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
            process.WaitForExit();
            if (process.ExitCode != 0) throw new InvalidOperationException("Real junction fixture unavailable: " + output);
        }
        public StoreActivation Activation() => WindowsLocalEvidenceStore.TryOpen(Path, StoreId, TrustedOperators);
        public IPrivateEvidenceStore Open()
        {
            var activation = Activation();
            Assert.True(activation.Store is not null, activation.Reason);
            return activation.Store!;
        }

        public void Dispose()
        {
            var exact = System.IO.Path.GetFullPath(Path);
            if (System.IO.Path.GetDirectoryName(exact) != parent.TrimEnd(System.IO.Path.DirectorySeparatorChar) ||
                System.IO.Path.GetFileName(exact) != ".storage-test-" + marker ||
                (File.GetAttributes(exact) & FileAttributes.ReparsePoint) != 0 ||
                File.ReadAllText(System.IO.Path.Combine(exact, ".fixture-id")) != marker ||
                File.ReadAllText(System.IO.Path.Combine(exact, ".fixture-parent")) != parent)
                throw new InvalidOperationException("Fixture cleanup identity mismatch.");
            RemoveLinks(exact);
            Directory.Delete(exact, true);
        }

        private static void RemoveLinks(string directory)
        {
            foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
            {
                var attributes = File.GetAttributes(entry);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                {
                    if ((attributes & FileAttributes.Directory) != 0) Directory.Delete(entry);
                    else File.Delete(entry);
                }
                else if ((attributes & FileAttributes.Directory) != 0) RemoveLinks(entry);
            }
        }
    }

    private sealed class TrackingSource(byte[] bytes, bool throwAfterBytes = false, Exception? failure = null) : Stream
    {
        private int position;
        public int Reads { get; private set; }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Reads++;
            if (position == bytes.Length && failure is not null) throw failure;
            if (position == bytes.Length && throwAfterBytes) throw new IOException("Injected source interruption.");
            var count = Math.Min(buffer.Length, bytes.Length - position);
            bytes.AsMemory(position, count).CopyTo(buffer);
            position += count;
            return ValueTask.FromResult(count);
        }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class WaitingSource : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}

public sealed class WindowsStorageFactAttribute : FactAttribute
{
    public WindowsStorageFactAttribute() { if (!OperatingSystem.IsWindows()) Skip = "Requires real Windows ACL and file sharing behavior."; }
}
public sealed class WindowsStorageTheoryAttribute : TheoryAttribute
{
    public WindowsStorageTheoryAttribute() { if (!OperatingSystem.IsWindows()) Skip = "Requires real Windows ACL and file sharing behavior."; }
}
