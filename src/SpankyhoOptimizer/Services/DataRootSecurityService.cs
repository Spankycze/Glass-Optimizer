using System;
using System.IO;
using System.Security.AccessControl;
using System.Security.Principal;

namespace SpankyhoOptimizer.Services;

// ProgramData is machine-wide and is consumed by elevated/SYSTEM components:
// the power-plan guard script executed by a SYSTEM task, AI ownership journals
// and the local update bridge. The default ProgramData ACL grants BUILTIN\Users
// write access, which would let any local user replace the guard script or an
// update payload. Keep users read-only; SYSTEM and Administrators retain full
// control. Changing the root DACL updates normally inherited children; only an
// explicitly ACL'd guard script is reset separately.
internal static class DataRootSecurityService
{
    internal static bool EnsureHardenedAcl()
    {
        try
        {
            Directory.CreateDirectory(LogService.Root);
            if (!UsersCanWrite(LogService.Root) &&
                (!File.Exists(GuardScriptPath) || SystemCanRead(GuardScriptPath)))
                return true;

            var security = new DirectorySecurity();
            security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            AddRule(security, WellKnownSidType.LocalSystemSid, FileSystemRights.FullControl);
            AddRule(security, WellKnownSidType.BuiltinAdministratorsSid, FileSystemRights.FullControl);
            AddRule(security, WellKnownSidType.BuiltinUsersSid, FileSystemRights.ReadAndExecute);
            new DirectoryInfo(LogService.Root).SetAccessControl(security);

            if (UsersCanWrite(LogService.Root))
            {
                LogService.Error("security", "DATA_ROOT_ACL_NOT_APPLIED");
                return false;
            }

            // A script created by an older build can carry an explicit ACL that
            // does not inherit the hardened root. Reset only this one file.
            if (File.Exists(GuardScriptPath) && !SystemCanRead(GuardScriptPath))
            {
                var fileSecurity = new FileSecurity();
                fileSecurity.SetAccessRuleProtection(isProtected: false, preserveInheritance: false);
                new FileInfo(GuardScriptPath).SetAccessControl(fileSecurity);
                if (!SystemCanRead(GuardScriptPath))
                {
                    LogService.Error("security", "DATA_ROOT_GUARD_ACL_UNREADABLE");
                    return false;
                }
            }

            LogService.Info("security", "DATA_ROOT_ACL_HARDENED users=read");
            return true;
        }
        catch (Exception ex)
        {
            LogService.Error("security", "DATA_ROOT_ACL_ERROR", ex);
            return false;
        }
    }

    private static void AddRule(DirectorySecurity security, WellKnownSidType sidType,
        FileSystemRights rights)
    {
        security.AddAccessRule(new FileSystemAccessRule(
            new SecurityIdentifier(sidType, null),
            rights,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
            PropagationFlags.None,
            AccessControlType.Allow));
    }

    private static string GuardScriptPath => Path.Combine(LogService.Root, "power-plan-guard.cmd");

    private static bool UsersCanWrite(string path)
    {
        try
        {
            DirectorySecurity acl = new DirectoryInfo(path).GetAccessControl();
            foreach (FileSystemAccessRule rule in acl.GetAccessRules(
                         includeExplicit: true, includeInherited: true,
                         targetType: typeof(SecurityIdentifier)))
            {
                if (rule.AccessControlType != AccessControlType.Allow ||
                    rule.IdentityReference is not SecurityIdentifier sid ||
                    !IsUnprivileged(sid))
                    continue;
                const FileSystemRights writeRights =
                    FileSystemRights.WriteData | FileSystemRights.AppendData |
                    FileSystemRights.WriteExtendedAttributes | FileSystemRights.WriteAttributes |
                    FileSystemRights.Delete | FileSystemRights.DeleteSubdirectoriesAndFiles |
                    FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership;
                if ((rule.FileSystemRights & writeRights) != 0)
                    return true;
            }
            return false;
        }
        catch (Exception ex)
        {
            // Fail closed: an unreadable ACL must still attempt the hardening pass.
            LogService.Error("security", "DATA_ROOT_ACL_READ_FAILED", ex);
            return true;
        }
    }

    private static bool SystemCanRead(string path)
    {
        try
        {
            FileSecurity acl = new FileInfo(path).GetAccessControl();
            foreach (FileSystemAccessRule rule in acl.GetAccessRules(
                         includeExplicit: true, includeInherited: true,
                         targetType: typeof(SecurityIdentifier)))
            {
                if (rule.AccessControlType != AccessControlType.Allow ||
                    rule.IdentityReference is not SecurityIdentifier sid ||
                    !sid.IsWellKnown(WellKnownSidType.LocalSystemSid))
                    continue;
                if ((rule.FileSystemRights & FileSystemRights.ReadData) != 0)
                    return true;
            }
            return false;
        }
        catch (Exception ex)
        {
            LogService.Error("security", "DATA_ROOT_GUARD_ACL_READ_FAILED", ex);
            return false;
        }
    }

    private static bool IsUnprivileged(SecurityIdentifier sid) =>
        sid.IsWellKnown(WellKnownSidType.BuiltinUsersSid) ||
        sid.IsWellKnown(WellKnownSidType.WorldSid) ||
        sid.IsWellKnown(WellKnownSidType.AuthenticatedUserSid);
}
