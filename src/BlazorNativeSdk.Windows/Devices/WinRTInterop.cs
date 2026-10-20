using System.Runtime.InteropServices;
using BlazorNativeSdk.WindowsPlatform.Interop;
using Windows.ApplicationModel.DataTransfer;
using Windows.Foundation;
using Windows.Security.Credentials.UI;
using WinRT;

namespace BlazorNativeSdk.WindowsPlatform;

/// <summary>
/// Calls the classic-COM "ForWindow" interop interfaces of WinRT activation factories through their vtables, so desktop
/// apps can parent WinRT UI (Windows Hello, Share) to an HWND without CsWin32 or built-in COM interop.
/// </summary>
internal static unsafe class WinRTInterop
{
    /// <summary>IID of <c>IUserConsentVerifierInterop</c> (userconsentverifierinterop.h).</summary>
    public static readonly Guid IID_IUserConsentVerifierInterop = new("39E050C3-4E74-441A-8DC0-B81104DF949C");

    /// <summary>IID of <c>IDataTransferManagerInterop</c> (shobjidl_core.h).</summary>
    public static readonly Guid IID_IDataTransferManagerInterop = new("3A3DCD6C-3EAB-43DC-BCDE-45671CE800C8");

    /// <summary>IID of <c>Windows.ApplicationModel.DataTransfer.IDataTransferManager</c>.</summary>
    public static readonly Guid IID_IDataTransferManager = new("A5CAEE9B-8708-49D1-8D36-67D25A8DA00C");

    /// <summary>
    /// IID of <c>IAsyncOperation&lt;UserConsentVerificationResult&gt;</c>: the parameterized-interface GUID computed from
    /// <c>pinterface({9fc2b0bb-e446-44e2-aa61-9cab8f636af2};enum(Windows.Security.Credentials.UI.UserConsentVerificationResult;i4))</c>.
    /// Hard-coded to avoid reflection; a unit test checks it against CsWinRT's generator.
    /// </summary>
    public static readonly Guid IID_IAsyncOperation_UserConsentVerificationResult = new("FD596FFD-2318-558F-9DBE-D21DF43764A5");

    /// <summary>
    /// <c>IUserConsentVerifierInterop::RequestVerificationForWindowAsync</c>: shows the Windows Hello prompt owned by
    /// <paramref name="hwnd"/>, so it appears in front of the app instead of behind it.
    /// </summary>
    public static IAsyncOperation<UserConsentVerificationResult> RequestVerificationForWindow(nint hwnd, string message)
    {
        using var factory = ActivationFactory.Get("Windows.Security.Credentials.UI.UserConsentVerifier", IID_IUserConsentVerifierInterop);
        var thisPtr = factory.ThisPtr;
        var vtable = *(void***)thisPtr;

        // IInspectable occupies slots 0-5; RequestVerificationForWindowAsync is slot 6.
        var requestVerification = (delegate* unmanaged<nint, nint, nint, Guid*, nint*, int>)vtable[6];

        var hstring = CreateHString(message);
        try
        {
            var iid = IID_IAsyncOperation_UserConsentVerificationResult;
            nint operation = 0;
            Marshal.ThrowExceptionForHR(requestVerification(thisPtr, hwnd, hstring, &iid, &operation));
            try
            {
                return MarshalInterface<IAsyncOperation<UserConsentVerificationResult>>.FromAbi(operation);
            }
            finally
            {
                _ = Marshal.Release(operation);
            }
        }
        finally
        {
            _ = NativeMethods.WindowsDeleteString(hstring);
        }
    }

    /// <summary><c>IDataTransferManagerInterop::GetForWindow</c>.</summary>
    public static DataTransferManager GetDataTransferManagerForWindow(nint hwnd)
    {
        using var factory = ActivationFactory.Get("Windows.ApplicationModel.DataTransfer.DataTransferManager", IID_IDataTransferManagerInterop);
        var thisPtr = factory.ThisPtr;
        var vtable = *(void***)thisPtr;

        // IUnknown occupies slots 0-2; GetForWindow is slot 3.
        var getForWindow = (delegate* unmanaged<nint, nint, Guid*, nint*, int>)vtable[3];
        var iid = IID_IDataTransferManager;
        nint manager = 0;
        Marshal.ThrowExceptionForHR(getForWindow(thisPtr, hwnd, &iid, &manager));
        try
        {
            return DataTransferManager.FromAbi(manager);
        }
        finally
        {
            _ = Marshal.Release(manager);
        }
    }

    /// <summary><c>IDataTransferManagerInterop::ShowShareUIForWindow</c>.</summary>
    public static void ShowShareUIForWindow(nint hwnd)
    {
        using var factory = ActivationFactory.Get("Windows.ApplicationModel.DataTransfer.DataTransferManager", IID_IDataTransferManagerInterop);
        var thisPtr = factory.ThisPtr;
        var vtable = *(void***)thisPtr;

        // Slot 4: ShowShareUIForWindow(HWND).
        var showShareUi = (delegate* unmanaged<nint, nint, int>)vtable[4];
        Marshal.ThrowExceptionForHR(showShareUi(thisPtr, hwnd));
    }

    private static nint CreateHString(string value)
    {
        nint hstring;
        fixed (char* chars = value)
        {
            Marshal.ThrowExceptionForHR(NativeMethods.WindowsCreateString(chars, (uint)value.Length, &hstring));
        }

        return hstring;
    }
}
