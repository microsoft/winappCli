// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.System.Com;

namespace Microsoft.Windows.SDK.BuildTools.WinApp.UIAutomation;

internal static unsafe class NativeUiAuditSnapshotArrays
{
    internal static int[] ReadIntegers(SAFEARRAY* array, int maximum, out bool truncated)
    {
        var count = Validate(array, VarEnum.VT_I4, sizeof(int));
        truncated = count > maximum;
        var length = (int)Math.Min(count, (uint)maximum);
        if (length == 0)
        {
            return [];
        }
        void* data = null;
        PInvoke.SafeArrayAccessData(array, &data).ThrowOnFailure();
        try
        {
            if (data is null)
            {
                throw new InvalidOperationException("The SAFEARRAY has no data.");
            }
            return new ReadOnlySpan<int>(data, length).ToArray();
        }
        finally
        {
            PInvoke.SafeArrayUnaccessData(array).ThrowOnFailure();
        }
    }

    internal static double[] ReadDoubles(SAFEARRAY* array, int maximum, out bool truncated)
    {
        var count = Validate(array, VarEnum.VT_R8, sizeof(double));
        truncated = count > maximum;
        var length = (int)Math.Min(count, (uint)maximum);
        if (length == 0)
        {
            return [];
        }
        void* data = null;
        PInvoke.SafeArrayAccessData(array, &data).ThrowOnFailure();
        try
        {
            if (data is null)
            {
                throw new InvalidOperationException("The SAFEARRAY has no data.");
            }
            return new ReadOnlySpan<double>(data, length).ToArray();
        }
        finally
        {
            PInvoke.SafeArrayUnaccessData(array).ThrowOnFailure();
        }
    }

    private static uint Validate(SAFEARRAY* array, VarEnum expectedType, uint elementSize)
    {
        if (array is null || array->cDims != 1 || array->cbElements != elementSize)
        {
            throw new InvalidOperationException("The provider returned a malformed SAFEARRAY.");
        }
        PInvoke.SafeArrayGetVartype(array, out var elementType).ThrowOnFailure();
        if ((int)elementType != (int)expectedType)
        {
            throw new InvalidOperationException("The SAFEARRAY element type does not match the UIA contract.");
        }
        return array->rgsabound[0].cElements;
    }
}
