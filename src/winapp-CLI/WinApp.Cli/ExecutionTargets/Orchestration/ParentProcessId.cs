// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using Windows.Win32;
using Windows.Win32.System.Diagnostics.ToolHelp;

namespace WinApp.Cli.ExecutionTargets.Orchestration;

/// <summary>
/// Finds a process's immediate parent, which Cooperative UI Turns uses as its default owner.
/// </summary>
/// <remarks>
/// Windows exposes no managed API for this. A toolhelp snapshot is used rather than
/// <c>NtQueryInformationProcess</c> because the latter is undocumented and needs a handle with
/// query rights, while the snapshot needs neither and is available in every session the agent or
/// host runs in.
/// <para>
/// The parent ID alone is never used as an identity: Windows reuses process IDs, so callers pair it
/// with the parent's start time before treating two commands as the same workflow.
/// </para>
/// </remarks>
internal static class ParentProcessId
{
    /// <summary>Returns the parent of <paramref name="processId"/>, or null when unavailable.</summary>
    public static unsafe int? TryGet(int processId)
    {
        using var snapshot = PInvoke.CreateToolhelp32Snapshot_SafeHandle(
            CREATE_TOOLHELP_SNAPSHOT_FLAGS.TH32CS_SNAPPROCESS,
            th32ProcessID: 0);

        if (snapshot.IsInvalid)
        {
            return null;
        }

        var entry = new PROCESSENTRY32W { dwSize = (uint)sizeof(PROCESSENTRY32W) };

        if (!PInvoke.Process32FirstW(snapshot, ref entry))
        {
            return null;
        }

        do
        {
            if (entry.th32ProcessID == (uint)processId)
            {
                var parent = (int)entry.th32ParentProcessID;

                // 0 is not a real parent; it is what the snapshot reports when there is none.
                return parent == 0 ? null : parent;
            }
        }
        while (PInvoke.Process32NextW(snapshot, ref entry));

        return null;
    }

    /// <summary>
    /// Every live descendant of <paramref name="processId"/> with its image name, from one snapshot. Children whose
    /// parent exited keep that parent's ID, so a reused ID can only add processes started after it; callers that act
    /// on a match check the image name as well.
    /// </summary>
    public static unsafe IReadOnlyList<(int ProcessId, string ImageName)> Descendants(int processId)
    {
        using var snapshot = PInvoke.CreateToolhelp32Snapshot_SafeHandle(
            CREATE_TOOLHELP_SNAPSHOT_FLAGS.TH32CS_SNAPPROCESS,
            th32ProcessID: 0);
        var result = new List<(int, string)>();
        if (snapshot.IsInvalid)
        {
            return result;
        }

        var children = new Dictionary<int, List<(int Id, string Name)>>();
        var entry = new PROCESSENTRY32W { dwSize = (uint)sizeof(PROCESSENTRY32W) };
        if (!PInvoke.Process32FirstW(snapshot, ref entry))
        {
            return result;
        }
        do
        {
            var parent = (int)entry.th32ParentProcessID;
            if (!children.TryGetValue(parent, out var list))
            {
                children[parent] = list = [];
            }
            list.Add(((int)entry.th32ProcessID, entry.szExeFile.ToString()));
        }
        while (PInvoke.Process32NextW(snapshot, ref entry));

        var pending = new Queue<int>([processId]);
        var seen = new HashSet<int> { processId };
        while (pending.Count > 0)
        {
            if (!children.TryGetValue(pending.Dequeue(), out var list))
            {
                continue;
            }
            foreach (var child in list)
            {
                if (seen.Add(child.Id))
                {
                    result.Add(child);
                    pending.Enqueue(child.Id);
                }
            }
        }
        return result;
    }
}
