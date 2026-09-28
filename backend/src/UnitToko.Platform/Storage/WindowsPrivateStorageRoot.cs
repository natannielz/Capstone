using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;

namespace UnitToko.Platform.Storage;

/// <summary>Observed Windows capability; checked namespace requires trusted owners not to mutate it.</summary>
[SupportedOSPlatform("windows")]
internal sealed class WindowsPrivateStorageRoot
{
    private readonly HashSet<string> trusted;
    private readonly string serviceSid;
    internal string Path { get; }
    internal WindowsPrivateStorageRoot(string path, IReadOnlyCollection<string> trustedOperatorSids)
    {
        ArgumentNullException.ThrowIfNull(trustedOperatorSids);
        if (!System.IO.Path.IsPathFullyQualified(path) || path.StartsWith(@"\\", StringComparison.Ordinal) ||
            path.Length < 3 || path[1] != ':' || path[2] is not ('\\' or '/'))
            throw new StorageUnavailableException("storage_root_invalid");
        Path = System.IO.Path.GetFullPath(path).TrimEnd(System.IO.Path.DirectorySeparatorChar);
        if (Path.Length < 4 || path.AsSpan(2).Contains(':') ||
            Path.Split(System.IO.Path.DirectorySeparatorChar).Any(segment => segment.EndsWith('.') || segment.EndsWith(' ')))
            throw new StorageUnavailableException("storage_root_invalid");
        using var identity = WindowsIdentity.GetCurrent();
        serviceSid = identity.User?.Value ?? throw new StorageUnavailableException("storage_identity_unavailable");
        var operators = trustedOperatorSids.Select(sid => new SecurityIdentifier(sid)).ToArray();
        // Supported local operator classes only; custom account/group membership is not verified here.
        if (operators.Any(sid => sid.Value != serviceSid && sid.Value is not ("S-1-5-18" or "S-1-5-32-544") &&
            !(sid.Value.StartsWith("S-1-5-80-", StringComparison.Ordinal) && sid.Value.Split('-').Length == 9)))
            throw new StorageUnavailableException("storage_operator_unsupported");
        trusted = new HashSet<string>(operators.Select(sid => sid.Value), StringComparer.Ordinal) { serviceSid };
        Validate();
    }
    internal void Validate()
    {
        var drive = new DriveInfo(System.IO.Path.GetPathRoot(Path)!);
        if (drive.DriveType != DriveType.Fixed || !string.Equals(drive.DriveFormat, "NTFS", StringComparison.OrdinalIgnoreCase))
            throw new StorageUnavailableException("storage_filesystem_unsupported");
        for (DirectoryInfo? directory = new(Path); directory is not null; directory = directory.Parent)
        {
            directory.Refresh();
            if (!directory.Exists || (directory.Attributes & FileAttributes.ReparsePoint) != 0 || directory.LinkTarget is not null)
                throw new StorageUnavailableException("storage_namespace_unsafe");
            var isRoot = string.Equals(directory.FullName.TrimEnd(System.IO.Path.DirectorySeparatorChar), Path, StringComparison.OrdinalIgnoreCase);
            ValidateSecurity(directory.GetAccessControl(AccessControlSections.Owner | AccessControlSections.Access), isRoot, isRoot, directory: true);
        }
    }
    internal bool ValidateLeaf(string path)
    {
        if (!string.Equals(System.IO.Path.GetDirectoryName(path), Path, StringComparison.OrdinalIgnoreCase))
            throw new StorageUnavailableException("storage_namespace_unsafe");
        try
        {
            var attributes = File.GetAttributes(path);
            if ((attributes & (FileAttributes.ReparsePoint | FileAttributes.Directory)) != 0 || new FileInfo(path).LinkTarget is not null)
                throw new StorageUnavailableException("storage_namespace_unsafe");
            return true;
        }
        catch (FileNotFoundException) { return false; }
        catch (DirectoryNotFoundException) { throw new StorageUnavailableException("storage_namespace_unavailable"); }
    }
    internal void ValidateFile(FileStream file) => ValidateSecurity(file.GetAccessControl(), true, false, directory: false);
    private void ValidateSecurity(FileSystemSecurity security, bool privateObject, bool requireProtected, bool directory)
    {
        var raw = new RawSecurityDescriptor(security.GetSecurityDescriptorBinaryForm(), 0);
        if (raw.Owner is null || !trusted.Contains(raw.Owner.Value) || raw.DiscretionaryAcl is null ||
            (requireProtected && !security.AreAccessRulesProtected))
            throw new StorageUnavailableException("storage_acl_unsafe");
        var serviceRights = 0u;
        foreach (GenericAce ace in raw.DiscretionaryAcl)
        {
            if (ace is not CommonAce common || common.IsCallback ||
                common.AceQualifier is not (AceQualifier.AccessAllowed or AceQualifier.AccessDenied))
                throw new StorageUnavailableException("storage_acl_unsupported");
            if (!privateObject && (common.AceFlags & AceFlags.InheritOnly) != 0) continue;
            var mask = ExpandGeneric(unchecked((uint)common.AccessMask));
            // Conservative rejection, not an effective-access evaluator for deny/conditional combinations.
            if (common.AceQualifier != AceQualifier.AccessAllowed) throw new StorageUnavailableException("storage_acl_unsupported");
            if (trusted.Contains(common.SecurityIdentifier.Value))
            {
                if (common.SecurityIdentifier.Value == serviceSid && (common.AceFlags & AceFlags.InheritOnly) == 0) serviceRights |= mask;
                continue;
            }
            var metadataRights = FileSystemRights.ReadAttributes | FileSystemRights.ReadPermissions | FileSystemRights.Synchronize;
            if (directory) metadataRights |= FileSystemRights.Traverse;
            var dangerous = privateObject
                ? (uint)(FileSystemRights.FullControl & ~metadataRights)
                : (uint)(FileSystemRights.WriteData | FileSystemRights.WriteExtendedAttributes | FileSystemRights.WriteAttributes |
                    FileSystemRights.DeleteSubdirectoriesAndFiles | FileSystemRights.Delete | FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership);
            if ((mask & dangerous) != 0) throw new StorageUnavailableException("storage_acl_unsafe");
        }
        var required = (uint)(FileSystemRights.ReadData | FileSystemRights.WriteData | FileSystemRights.ReadPermissions);
        if (privateObject && (serviceRights & required) != required) throw new StorageUnavailableException("storage_service_access_unverified");
    }
    private static uint ExpandGeneric(uint mask)
    {
        if ((mask & 0x10000000) != 0) mask |= (uint)FileSystemRights.FullControl;
        if ((mask & 0x80000000) != 0) mask |= (uint)(FileSystemRights.Read | FileSystemRights.Synchronize);
        if ((mask & 0x40000000) != 0) mask |= (uint)(FileSystemRights.Write | FileSystemRights.ReadPermissions | FileSystemRights.Synchronize);
        if ((mask & 0x20000000) != 0) mask |= (uint)(FileSystemRights.Traverse | FileSystemRights.ReadAttributes | FileSystemRights.ReadPermissions | FileSystemRights.Synchronize);
        return mask;
    }
}
